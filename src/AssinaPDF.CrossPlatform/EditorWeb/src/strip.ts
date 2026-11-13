/**
 * Removes original glyphs from a PDF's page content streams, so edited/moved text
 * leaves the real page background behind instead of a white cover.
 *
 * It runs a tiny content-stream interpreter (graphics + text state), finds every glyph
 * whose centre falls inside a target box, and rewrites the show-text operator with the
 * glyph replaced by equivalent spacing, so all other glyphs keep their exact position.
 */
import {
  PDFArray,
  PDFDict,
  PDFDocument,
  PDFName,
  PDFNumber,
  PDFRawStream,
  PDFStream,
  StandardFontEmbedder,
  StandardFonts,
  decodePDFRawStream,
  type PDFPage,
} from "pdf-lib";
import type { Box } from "./types";

export interface StripTarget {
  id: string;
  page: number;
  box: Box;
}

/** Original image paint operation to remove from a page before drawing its moved copy. */
export interface StripImageTarget extends StripTarget {}

type Matrix = [number, number, number, number, number, number];
const IDENTITY: Matrix = [1, 0, 0, 1, 0, 0];
const mul = (m: Matrix, n: Matrix): Matrix => [
  m[0] * n[0] + m[1] * n[2],
  m[0] * n[1] + m[1] * n[3],
  m[2] * n[0] + m[3] * n[2],
  m[2] * n[1] + m[3] * n[3],
  m[4] * n[0] + m[5] * n[2] + n[4],
  m[4] * n[1] + m[5] * n[3] + n[5],
];
const apply = (m: Matrix, x: number, y: number) => ({ x: x * m[0] + y * m[2] + m[4], y: x * m[1] + y * m[3] + m[5] });

// ---------------------------------------------------------------- tokenizer

type Token =
  | { t: "num"; v: number; s: number; e: number }
  | { t: "name"; v: string; s: number; e: number }
  | { t: "str"; v: Uint8Array; s: number; e: number }
  | { t: "["; s: number; e: number }
  | { t: "]"; s: number; e: number }
  | { t: "<<"; s: number; e: number }
  | { t: ">>"; s: number; e: number }
  | { t: "op"; v: string; s: number; e: number };

const isWs = (c: number) => c === 0 || c === 9 || c === 10 || c === 12 || c === 13 || c === 32;
const isDelim = (c: number) => c === 40 || c === 41 || c === 60 || c === 62 || c === 91 || c === 93 || c === 123 || c === 125 || c === 47 || c === 37;

function* tokenize(b: Uint8Array): Generator<Token> {
  let i = 0;
  const n = b.length;
  while (i < n) {
    const c = b[i]!;
    if (isWs(c)) {
      i++;
      continue;
    }
    if (c === 37) {
      // comment
      while (i < n && b[i] !== 10 && b[i] !== 13) i++;
      continue;
    }
    const s = i;
    if (c === 40) {
      // literal string
      const out: number[] = [];
      let depth = 1;
      i++;
      while (i < n && depth > 0) {
        const ch = b[i]!;
        if (ch === 92) {
          const nx = b[i + 1] ?? 0;
          i += 2;
          if (nx === 110) out.push(10);
          else if (nx === 114) out.push(13);
          else if (nx === 116) out.push(9);
          else if (nx === 98) out.push(8);
          else if (nx === 102) out.push(12);
          else if (nx === 13) {
            if (b[i] === 10) i++;
          } else if (nx === 10) {
            /* line continuation */
          } else if (nx >= 48 && nx <= 55) {
            let v = nx - 48;
            for (let k = 0; k < 2 && (b[i] ?? 0) >= 48 && (b[i] ?? 0) <= 55; k++) v = v * 8 + (b[i++]! - 48);
            out.push(v & 255);
          } else out.push(nx);
          continue;
        }
        if (ch === 40) depth++;
        else if (ch === 41) {
          depth--;
          if (depth === 0) {
            i++;
            break;
          }
        }
        out.push(ch);
        i++;
      }
      yield { t: "str", v: Uint8Array.from(out), s, e: i };
      continue;
    }
    if (c === 60) {
      if (b[i + 1] === 60) {
        i += 2;
        yield { t: "<<", s, e: i };
        continue;
      }
      i++;
      let hex = "";
      while (i < n && b[i] !== 62) {
        if (!isWs(b[i]!)) hex += String.fromCharCode(b[i]!);
        i++;
      }
      i++;
      if (hex.length % 2) hex += "0";
      const out = new Uint8Array(hex.length / 2);
      for (let k = 0; k < out.length; k++) out[k] = parseInt(hex.slice(k * 2, k * 2 + 2), 16) || 0;
      yield { t: "str", v: out, s, e: i };
      continue;
    }
    if (c === 62 && b[i + 1] === 62) {
      i += 2;
      yield { t: ">>", s, e: i };
      continue;
    }
    if (c === 91 || c === 93) {
      i++;
      yield { t: c === 91 ? "[" : "]", s, e: i };
      continue;
    }
    if (c === 47) {
      i++;
      while (i < n && !isWs(b[i]!) && !isDelim(b[i]!)) i++;
      yield { t: "name", v: new TextDecoder("latin1").decode(b.subarray(s + 1, i)), s, e: i };
      continue;
    }
    if (c === 123 || c === 125 || c === 41 || c === 62) {
      i++;
      continue;
    }
    while (i < n && !isWs(b[i]!) && !isDelim(b[i]!)) i++;
    const word = new TextDecoder("latin1").decode(b.subarray(s, i));
    if (/^[+-]?(\d+\.?\d*|\.\d+)$/.test(word)) {
      yield { t: "num", v: parseFloat(word), s, e: i };
      continue;
    }
    if (word === "ID") {
      // inline image data: skip until whitespace + EI + whitespace
      i++;
      while (i < n) {
        if (isWs(b[i - 1] ?? 32) && b[i] === 69 && b[i + 1] === 73 && (i + 2 >= n || isWs(b[i + 2]!))) {
          i += 2;
          break;
        }
        i++;
      }
      yield { t: "op", v: "EI", s, e: i };
      continue;
    }
    yield { t: "op", v: word, s, e: i };
  }
}

// ---------------------------------------------------------------- fonts

interface FontInfo {
  twoByte: boolean;
  /** Width in 1/1000 text-space units. */
  width: (code: number) => number;
}

const STD_NAMES: Record<string, StandardFonts> = {
  helvetica: StandardFonts.Helvetica,
  "helvetica-bold": StandardFonts.HelveticaBold,
  "helvetica-oblique": StandardFonts.HelveticaOblique,
  "helvetica-boldoblique": StandardFonts.HelveticaBoldOblique,
  "times-roman": StandardFonts.TimesRoman,
  "times-bold": StandardFonts.TimesRomanBold,
  "times-italic": StandardFonts.TimesRomanItalic,
  "times-bolditalic": StandardFonts.TimesRomanBoldItalic,
  courier: StandardFonts.Courier,
  "courier-bold": StandardFonts.CourierBold,
  "courier-oblique": StandardFonts.CourierOblique,
  "courier-boldoblique": StandardFonts.CourierBoldOblique,
  symbol: StandardFonts.Symbol,
  zapfdingbats: StandardFonts.ZapfDingbats,
};

const num = (o: unknown, fallback = 0) => (o instanceof PDFNumber ? o.asNumber() : fallback);

function readFont(dict: PDFDict | undefined): FontInfo {
  const fallback: FontInfo = { twoByte: false, width: () => 500 };
  if (!dict) return fallback;
  const subtype = dict.lookup(PDFName.of("Subtype"));
  const sub = subtype instanceof PDFName ? subtype.asString() : "";

  if (sub === "/Type0") {
    const desc = dict.lookup(PDFName.of("DescendantFonts"));
    const cid = desc instanceof PDFArray ? desc.lookup(0) : undefined;
    const widths = new Map<number, number>();
    let dw = 1000;
    if (cid instanceof PDFDict) {
      dw = num(cid.lookup(PDFName.of("DW")), 1000);
      const w = cid.lookup(PDFName.of("W"));
      if (w instanceof PDFArray) {
        let k = 0;
        while (k < w.size()) {
          const first = num(w.lookup(k));
          const next = w.lookup(k + 1);
          if (next instanceof PDFArray) {
            for (let j = 0; j < next.size(); j++) widths.set(first + j, num(next.lookup(j)));
            k += 2;
          } else {
            const last = num(next);
            const val = num(w.lookup(k + 2));
            for (let c = first; c <= last && c - first < 65536; c++) widths.set(c, val);
            k += 3;
          }
        }
      }
    }
    return { twoByte: true, width: (c) => widths.get(c) ?? dw };
  }

  const widthsArr = dict.lookup(PDFName.of("Widths"));
  const firstChar = num(dict.lookup(PDFName.of("FirstChar")), 0);
  let scale = 1;
  if (sub === "/Type3") {
    const fm = dict.lookup(PDFName.of("FontMatrix"));
    scale = fm instanceof PDFArray ? num(fm.lookup(0), 0.001) * 1000 : 1;
  }
  const descriptor = dict.lookup(PDFName.of("FontDescriptor"));
  const missing = descriptor instanceof PDFDict ? num(descriptor.lookup(PDFName.of("MissingWidth")), 0) : 0;

  if (widthsArr instanceof PDFArray) {
    const ws = widthsArr.asArray().map((_, i) => num(widthsArr.lookup(i)));
    return { twoByte: false, width: (c) => (ws[c - firstChar] ?? missing ?? 500) * scale || missing || 500 };
  }

  const base = dict.lookup(PDFName.of("BaseFont"));
  const baseName = base instanceof PDFName ? base.asString().slice(1).replace(/^[A-Z]{6}\+/, "").toLowerCase() : "";
  const std = STD_NAMES[baseName];
  if (std) {
    const embedder = StandardFontEmbedder.for(std as unknown as Parameters<typeof StandardFontEmbedder.for>[0]);
    const cache = new Map<number, number>();
    return {
      twoByte: false,
      width: (c) => {
        let w = cache.get(c);
        if (w === undefined) {
          try {
            w = embedder.widthOfTextAtSize(String.fromCharCode(c), 1000);
          } catch {
            w = 500;
          }
          cache.set(c, w);
        }
        return w;
      },
    };
  }
  return fallback;
}

// ---------------------------------------------------------------- interpreter

type Val = number | string | Uint8Array | Val[] | { dict: true };

interface GState {
  ctm: Matrix;
  tc: number;
  tw: number;
  th: number;
  tl: number;
  ts: number;
  tfs: number;
  font: FontInfo;
  fill: string;
}

export type GlyphProbe = (x: number, y: number, color: string) => void;

const hex2 = (v: number) => Math.round(Math.min(1, Math.max(0, v)) * 255).toString(16).padStart(2, "0");
const rgbHex = (r: number, g: number, b: number) => `#${hex2(r)}${hex2(g)}${hex2(b)}`;
const cmykHex = (c: number, m: number, y: number, k: number) => rgbHex((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
function colorFrom(nums: number[]): string | null {
  if (nums.length === 1) return rgbHex(nums[0]!, nums[0]!, nums[0]!);
  if (nums.length === 3) return rgbHex(nums[0]!, nums[1]!, nums[2]!);
  if (nums.length === 4) return cmykHex(nums[0]!, nums[1]!, nums[2]!, nums[3]!);
  return null;
}

const toHex = (bytes: Uint8Array) => {
  let s = "<";
  for (const x of bytes) s += x.toString(16).padStart(2, "0");
  return s + ">";
};
const fmt = (n: number) => (Math.round(n * 1000) / 1000).toString();

function processStream(
  bytes: Uint8Array,
  page: PDFPage,
  boxes: StripTarget[],
  matched: Set<string>,
  probe?: GlyphProbe,
  imageTargets: StripImageTarget[] = [],
): Uint8Array | null {
  const crop = page.getCropBox();
  const resources = page.node.normalizedEntries().Resources;
  const fontsDict = resources.lookup(PDFName.of("Font"));
  const fontCache = new Map<string, FontInfo>();
  const getFont = (name: string) => {
    let f = fontCache.get(name);
    if (!f) {
      const d = fontsDict instanceof PDFDict ? fontsDict.lookup(PDFName.of(name)) : undefined;
      f = readFont(d instanceof PDFDict ? d : undefined);
      fontCache.set(name, f);
    }
    return f;
  };

  let gs: GState = { ctm: [...IDENTITY], tc: 0, tw: 0, th: 1, tl: 0, ts: 0, tfs: 0, font: { twoByte: false, width: () => 500 }, fill: "#000000" };
  const stack: GState[] = [];
  let tm: Matrix = [...IDENTITY];
  let tlm: Matrix = [...IDENTITY];

  const chunks: Uint8Array[] = [];
  let copied = 0;
  let changed = false;
  const enc = new TextEncoder();

  let operands: Val[] = [];
  const nest: Val[][] = [];
  let operandStart = -1;

  const hit = (ux: number, uy: number) => {
    const x = ux - crop.x;
    const y = crop.y + crop.height - uy;
    for (const t of boxes) {
      const b = t.box;
      if (x >= b.x && x <= b.x + b.width && y >= b.y && y <= b.y + b.height) return t.id;
    }
    return null;
  };

  // PDF image XObjects paint a unit square through the current transformation matrix.
  // Match by area overlap rather than only the centre, so clipped/scaled images still match.
  const hitImage = () => {
    if (!imageTargets.length) return null;
    const corners = [[0, 0], [1, 0], [0, 1], [1, 1]].map(([x, y]) => apply(gs.ctm, x!, y!));
    const left = Math.min(...corners.map(p => p.x)) - crop.x;
    const right = Math.max(...corners.map(p => p.x)) - crop.x;
    const top = crop.y + crop.height - Math.max(...corners.map(p => p.y));
    const bottom = crop.y + crop.height - Math.min(...corners.map(p => p.y));
    const area = Math.max(0, right - left) * Math.max(0, bottom - top);
    if (area <= 0) return null;
    for (const target of imageTargets) {
      const b = target.box;
      const iw = Math.max(0, Math.min(right, b.x + b.width) - Math.max(left, b.x));
      const ih = Math.max(0, Math.min(bottom, b.y + b.height) - Math.max(top, b.y));
      const overlap = iw * ih;
      const targetArea = Math.max(1, b.width * b.height);
      if (overlap / Math.min(area, targetArea) >= 0.8) return target.id;
    }
    return null;
  };

  const nextLine = () => {
    tlm = mul([1, 0, 0, 1, 0, -gs.tl], tlm);
    tm = [...tlm];
  };

  /** Shows text, advancing Tm. Returns rewritten TJ array source if glyphs were removed. */
  const show = (items: Val[]): string | null => {
    const scale = gs.tfs * gs.th;
    const out: string[] = [];
    let removedAny = false;
    let pendingKern = 0;
    for (const item of items) {
      if (typeof item === "number") {
        const dx = (-item / 1000) * scale;
        tm = mul([1, 0, 0, 1, dx, 0], tm);
        pendingKern += item;
        continue;
      }
      if (!(item instanceof Uint8Array)) continue;
      const step = gs.font.twoByte ? 2 : 1;
      let kept: number[] = [];
      const flushKept = () => {
        if (pendingKern) out.push(fmt(pendingKern));
        pendingKern = 0;
        if (kept.length) out.push(toHex(Uint8Array.from(kept)));
        kept = [];
      };
      for (let k = 0; k + step <= item.length; k += step) {
        const code = step === 2 ? (item[k]! << 8) | item[k + 1]! : item[k]!;
        const w0 = gs.font.width(code) / 1000;
        const isSpace = step === 1 && code === 32;
        const adv = (w0 * gs.tfs + gs.tc + (isSpace ? gs.tw : 0)) * gs.th;
        const trm = mul(tm, gs.ctm);
        const centre = apply(trm, adv / 2, gs.ts + gs.tfs * 0.35);
        if (probe) probe(centre.x - crop.x, crop.y + crop.height - centre.y, gs.fill);
        const id = scale && boxes.length ? hit(centre.x, centre.y) : null;
        if (id) {
          matched.add(id);
          removedAny = true;
          if (kept.length) flushKept();
          pendingKern += (-adv * 1000) / scale;
        } else {
          if (pendingKern) flushKept();
          for (let j = 0; j < step; j++) kept.push(item[k + j]!);
        }
        tm = mul([1, 0, 0, 1, adv, 0], tm);
      }
      flushKept();
    }
    if (!removedAny) return null;
    if (pendingKern) out.push(fmt(pendingKern));
    return `[${out.join(" ")}] TJ`;
  };

  const replace = (start: number, end: number, text: string) => {
    chunks.push(bytes.subarray(copied, start), enc.encode(` ${text} `));
    copied = end;
    changed = true;
  };

  for (const tok of tokenize(bytes)) {
    if (tok.t !== "op" && operandStart < 0) operandStart = tok.s;
    const push = (v: Val) => {
      const top = nest[nest.length - 1];
      if (top) top.push(v);
      else operands.push(v);
    };
    switch (tok.t) {
      case "num":
      case "name":
      case "str":
        push(tok.v);
        continue;
      case "[":
      case "<<":
        nest.push([]);
        continue;
      case "]": {
        const arr = nest.pop() ?? [];
        push(arr);
        continue;
      }
      case ">>":
        nest.pop();
        push({ dict: true });
        continue;
    }

    const op = tok.v;
    const start = operandStart >= 0 ? operandStart : tok.s;
    const o = operands;
    const n = (i: number) => (typeof o[i] === "number" ? (o[i] as number) : 0);
    operands = [];
    operandStart = -1;
    nest.length = 0;

    switch (op) {
      case "q":
        stack.push({ ...gs, ctm: [...gs.ctm] });
        break;
      case "Q":
        gs = stack.pop() ?? gs;
        break;
      case "cm":
        gs.ctm = mul([n(0), n(1), n(2), n(3), n(4), n(5)], gs.ctm);
        break;
      case "BT":
        tm = [...IDENTITY];
        tlm = [...IDENTITY];
        break;
      case "Tm":
        tlm = [n(0), n(1), n(2), n(3), n(4), n(5)];
        tm = [...tlm];
        break;
      case "Td":
        tlm = mul([1, 0, 0, 1, n(0), n(1)], tlm);
        tm = [...tlm];
        break;
      case "TD":
        gs.tl = -n(1);
        tlm = mul([1, 0, 0, 1, n(0), n(1)], tlm);
        tm = [...tlm];
        break;
      case "T*":
        nextLine();
        break;
      case "TL":
        gs.tl = n(0);
        break;
      case "Tc":
        gs.tc = n(0);
        break;
      case "Tw":
        gs.tw = n(0);
        break;
      case "Tz":
        gs.th = n(0) / 100;
        break;
      case "Ts":
        gs.ts = n(0);
        break;
      case "rg":
      case "g":
      case "k":
      case "sc":
      case "scn": {
        const c = colorFrom(o.filter((v): v is number => typeof v === "number"));
        if (c) gs.fill = c;
        break;
      }
      case "Tf":
        gs.font = getFont(typeof o[0] === "string" ? o[0] : "");
        gs.tfs = n(1);
        break;
      case "Do": {
        const name = typeof o[0] === "string" ? o[0] : "";
        const xobjects = resources.lookup(PDFName.of("XObject"));
        const xobject = xobjects instanceof PDFDict ? xobjects.lookup(PDFName.of(name)) : undefined;
        const subtype = xobject instanceof PDFStream ? xobject.dict.lookup(PDFName.of("Subtype")) : undefined;
        if (subtype instanceof PDFName && subtype.asString() === "/Image") {
          const id = hitImage();
          if (id) {
            matched.add(id);
            replace(start, tok.e, "q Q");
          }
        }
        break;
      }
      case "Tj": {
        const r = show([o[0] as Val]);
        if (r) replace(start, tok.e, r);
        break;
      }
      case "TJ": {
        const r = show(Array.isArray(o[0]) ? (o[0] as Val[]) : []);
        if (r) replace(start, tok.e, r);
        break;
      }
      case "'": {
        nextLine();
        const r = show([o[0] as Val]);
        if (r) replace(start, tok.e, `T* ${r}`);
        break;
      }
      case '"': {
        gs.tw = n(0);
        gs.tc = n(1);
        nextLine();
        const r = show([o[2] as Val]);
        if (r) replace(start, tok.e, `${fmt(n(0))} Tw ${fmt(n(1))} Tc T* ${r}`);
        break;
      }
    }
  }

  if (!changed) return null;
  chunks.push(bytes.subarray(copied));
  const total = chunks.reduce((s, c) => s + c.length, 0);
  const result = new Uint8Array(total);
  let off = 0;
  for (const c of chunks) {
    result.set(c, off);
    off += c.length;
  }
  return result;
}

function pageContents(pdf: PDFDocument, page: PDFPage): Uint8Array | null {
  const contents = page.node.Contents();
  if (!contents) return null;
  const streams: PDFStream[] = [];
  if (contents instanceof PDFArray) {
    for (let i = 0; i < contents.size(); i++) {
      const s = contents.lookup(i);
      if (s instanceof PDFStream) streams.push(s);
    }
  } else if (contents instanceof PDFStream) streams.push(contents);
  const parts: Uint8Array[] = [];
  for (const s of streams) {
    if (s instanceof PDFRawStream) parts.push(decodePDFRawStream(s).decode());
    else if ("getUnencodedContents" in s && typeof s.getUnencodedContents === "function") parts.push(s.getUnencodedContents() as Uint8Array);
    else parts.push(s.getContents());
    parts.push(new Uint8Array([10]));
  }
  void pdf;
  const total = parts.reduce((a, p) => a + p.length, 0);
  const out = new Uint8Array(total);
  let off = 0;
  for (const p of parts) {
    out.set(p, off);
    off += p.length;
  }
  return out;
}

/** Removes target glyphs in place on an already-loaded document. Returns ids that were found. */
export function stripTextInDocument(pdf: PDFDocument, targets: StripTarget[], imageTargets: StripImageTarget[] = []): Set<string> {
  const matched = new Set<string>();
  const byPage = new Map<number, StripTarget[]>();
  for (const t of [...targets, ...imageTargets]) byPage.set(t.page, [...(byPage.get(t.page) ?? []), t]);
  const pages = pdf.getPages();
  for (const index of byPage.keys()) {
    const page = pages[index];
    if (!page || page.getRotation().angle % 360 !== 0) continue;
    try {
      const content = pageContents(pdf, page);
      if (!content) continue;
      const pageImages = imageTargets.filter(t => t.page === index);
      const pageTexts = targets.filter(t => t.page === index);
      const next = processStream(content, page, pageTexts, matched, undefined, pageImages);
      if (next) {
        const ref = pdf.context.register(pdf.context.flateStream(next));
        page.node.set(PDFName.of("Contents"), pdf.context.obj([ref]));
      }
    } catch (err) {
      console.warn("Não foi possível remover o texto original da página", index + 1, err);
    }
  }
  return matched;
}

/** Reports every glyph's position (top-left page points) and fill colour, per page. */
export async function scanGlyphColors(original: Uint8Array) {
  const pdf = await PDFDocument.load(original, { ignoreEncryption: true, updateMetadata: false });
  return pdf.getPages().map((page) => {
    const points: { x: number; y: number; color: string }[] = [];
    if (page.getRotation().angle % 360 !== 0) return points;
    try {
      const content = pageContents(pdf, page);
      if (content) processStream(content, page, [], new Set(), (x, y, color) => points.push({ x, y, color }));
    } catch {
      /* colours are optional */
    }
    return points;
  });
}

export async function stripText(original: Uint8Array, targets: StripTarget[], imageTargets: StripImageTarget[] = []) {
  const pdf = await PDFDocument.load(original, { ignoreEncryption: true, updateMetadata: false });
  const matched = stripTextInDocument(pdf, targets, imageTargets);
  const bytes = await pdf.save({ useObjectStreams: false });
  return { bytes, matched };
}
