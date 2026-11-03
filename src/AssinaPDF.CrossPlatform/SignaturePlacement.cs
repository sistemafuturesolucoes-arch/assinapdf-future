namespace AssinaPDF.MacPrototype;

public sealed record SignaturePlacement(
    int PageIndex,
    double NormalizedX,
    double NormalizedY,
    float PageWidthPoints,
    float PageHeightPoints);

