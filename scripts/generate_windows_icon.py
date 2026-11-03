from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src" / "AssinaPDF.CrossPlatform" / "logo-future.png"
OUTPUT = ROOT / "src" / "AssinaPDF.CrossPlatform" / "Assets" / "assinapdf.ico"

if not SOURCE.is_file():
    raise SystemExit(f"Logo PNG oficial não encontrada: {SOURCE}")

OUTPUT.parent.mkdir(parents=True, exist_ok=True)

logo = Image.open(SOURCE).convert("RGBA")
canvas_size = 512
canvas = Image.new("RGBA", (canvas_size, canvas_size), (255, 255, 255, 0))

# Mantém a proporção original da logo e centraliza em uma tela quadrada
# para produzir tamanhos de ícone compatíveis com Windows Explorer e atalhos.
logo.thumbnail((460, 230), Image.Resampling.LANCZOS)
position = ((canvas_size - logo.width) // 2, (canvas_size - logo.height) // 2)
canvas.alpha_composite(logo, position)

canvas.save(
    OUTPUT,
    format="ICO",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)

print(f"Ícone Windows criado: {OUTPUT}")
