namespace AssinaPDF.MacPrototype;

public sealed record SignaturePlacement(
    int PageIndex,
    double NormalizedX,
    double NormalizedY,
    double NormalizedWidth,
    double NormalizedHeight,
    float PageWidthPoints,
    float PageHeightPoints,
    int PageRotation = 0);
