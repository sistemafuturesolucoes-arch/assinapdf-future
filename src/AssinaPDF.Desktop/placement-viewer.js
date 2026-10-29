import * as pdfjsLib from "./viewer/pdf.mjs";
pdfjsLib.GlobalWorkerOptions.workerSrc = "./viewer/pdf.worker.mjs";
const pages = document.getElementById("pages");
const fileUrl = new URLSearchParams(location.search).get("file");
const placements = [];
window.clearSelections = () => { placements.splice(0, placements.length); document.querySelectorAll(".marker").forEach(el => el.remove()); publish(); };
function publish(){ window.chrome.webview.postMessage({placements}); }
function showError(message){ pages.innerHTML = ""; const el=document.createElement("div"); el.className="error"; el.textContent=message; pages.appendChild(el); }
try {
 if (!fileUrl) throw new Error("O caminho do PDF não foi informado.");
 const pdf = await pdfjsLib.getDocument({url:fileUrl}).promise;
 pages.innerHTML = "";
 for(let pageNo=1; pageNo<=pdf.numPages; pageNo++){
  const page=await pdf.getPage(pageNo);
  const base=page.getViewport({scale:1});
  const maxWidth=Math.min(1000, Math.max(500, window.innerWidth-70));
  const scale=Math.min(1.45, maxWidth/base.width);
  const viewport=page.getViewport({scale});
  const wrapper=document.createElement("div"); wrapper.className="page"; wrapper.style.width=viewport.width+"px";
  const canvas=document.createElement("canvas"); const ctx=canvas.getContext("2d");
  const dpr=window.devicePixelRatio||1;
  canvas.width=Math.floor(viewport.width*dpr); canvas.height=Math.floor(viewport.height*dpr);
  canvas.style.width=viewport.width+"px"; canvas.style.height=viewport.height+"px";
  wrapper.appendChild(canvas); pages.appendChild(wrapper);
  await page.render({canvasContext:ctx,viewport:page.getViewport({scale:scale*dpr})}).promise;
  canvas.addEventListener("click",event=>{
   const rect=canvas.getBoundingClientRect(); const px=event.clientX-rect.left; const py=event.clientY-rect.top;
   const [pdfX,pdfY]=viewport.convertToPdfPoint(px,py);
   const item={page:pageNo,x:Math.max(0,pdfX),y:Math.max(0,pdfY)};
   placements.push(item);
   const marker=document.createElement("div"); marker.className="marker"; marker.textContent="Assinatura "+placements.length;
   marker.style.left=(px/viewport.width*100)+"%"; marker.style.top=(py/viewport.height*100)+"%";
   wrapper.appendChild(marker); publish();
  });
 }
} catch(e) { showError("Não foi possível renderizar este PDF para seleção. "+(e?.message||e)); }
