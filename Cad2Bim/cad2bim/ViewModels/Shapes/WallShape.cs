using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace Cad2Bim.ViewModels.Shapes {
    // A classified wall: its two parallel edge segments.
    public record WallShape(SegmentShape A, SegmentShape B);
}
