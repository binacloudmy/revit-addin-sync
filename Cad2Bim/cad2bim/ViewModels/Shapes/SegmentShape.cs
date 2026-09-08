using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace Cad2Bim.ViewModels.Shapes {
    // Render-ready, immutable, in raw CAD coordinates (Y up).
    public record SegmentShape(double X1, double Y1, double X2, double Y2);
}
