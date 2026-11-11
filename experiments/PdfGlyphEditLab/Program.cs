using PdfGlyphEditLab;

var passed = 0;
Run("selection spanning split text runs", () =>
{
    var glyphs = new[]
    {
        new GlyphBox(0, "A", 10, 10, 18, 22),
        new GlyphBox(1, "B", 18, 10, 26, 22),
        new GlyphBox(2, "C", 26, 10, 34, 22),
        new GlyphBox(3, "D", 34, 10, 42, 22)
    };
    var result = GlyphSelectionPlanner.Select(glyphs, new SelectionRect(17, 9, 35, 23));
    Assert(result.Select(x => x.CharacterIndex).SequenceEqual(new[] { 1, 2, 3 }),
        "Expected B, C and D even when the source text is represented by separate runs.");
});

Run("partial selection keeps adjacent glyphs outside", () =>
{
    var glyphs = new[]
    {
        new GlyphBox(10, "L", 0, 0, 8, 10),
        new GlyphBox(11, "M", 8, 0, 16, 10),
        new GlyphBox(12, "N", 16, 0, 24, 10)
    };
    var result = GlyphSelectionPlanner.Select(glyphs, new SelectionRect(8.5, 1, 15.5, 9));
    Assert(result.Select(x => x.Text).SequenceEqual(new[] { "M" }),
        "A narrow selection must not consume neighboring glyphs.");
});

Run("no intersection selects nothing", () =>
{
    var glyphs = new[] { new GlyphBox(0, "X", 0, 0, 8, 10) };
    var result = GlyphSelectionPlanner.Select(glyphs, new SelectionRect(20, 20, 30, 30));
    Assert(result.Count == 0, "A distant selection must be empty.");
});

Console.WriteLine($"PDF glyph lab: {passed}/3 checks passed.");

void Run(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine($"PASS: {name}");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
