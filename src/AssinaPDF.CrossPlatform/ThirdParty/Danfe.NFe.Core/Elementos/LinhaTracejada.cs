using System;
using System.Drawing;
using PdfSharp.Drawing;
using Danfe.NFe.Core.Graphics;

namespace Danfe.NFe.Core.Elementos
{
    internal class LinhaTracejada : DrawableBase
    {
        public float Margin { get; set; }
        public double[] DashPattern { get; set; } = new double[] { 3, 2 };

        public LinhaTracejada(float margin)
        {
            Margin = margin;
        }

        public override void Draw(Gfx gfx)
        {
            base.Draw(gfx);

            gfx.DrawDashedLine(
                new PointF(BoundingBox.Left, Y + Margin),
                new PointF(BoundingBox.Right, Y + Margin),
                XColors.Black,
                0.25F,
                DashPattern);
        }

        public override float Height { get => 2 * Margin; set => throw new NotSupportedException(); }
    }
}
