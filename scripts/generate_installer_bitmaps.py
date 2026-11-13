from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
LOGO = ROOT / "src" / "AssinaPDF.CrossPlatform" / "logo-future.png"
OUT = ROOT / "installer-crossplatform"

def make_bitmap(size: tuple[int, int], max_logo: tuple[int, int], centered: bool, filename: str) -> None:
    if not LOGO.is_file():
        raise FileNotFoundError(f"Logo oficial não encontrada: {LOGO}")

    with Image.open(LOGO) as source:
        logo = source.convert("RGBA")
    logo.thumbnail(max_logo, Image.Resampling.LANCZOS)

    canvas = Image.new("RGB", size, "white")
    x = (size[0] - logo.width) // 2 if centered else 12
    y = (size[1] - logo.height) // 2
    canvas.paste(logo, (x, y), logo)
    destination = OUT / filename
    canvas.save(destination, format="BMP")
    print(f"Imagem do instalador criada: {destination}")

def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    # Dimensões exigidas pela interface padrão do WiX.
    make_bitmap((493, 58), (210, 46), False, "FutureInstallerBanner.bmp")
    make_bitmap((493, 312), (190, 190), True, "FutureInstallerDialog.bmp")

if __name__ == "__main__":
    main()
