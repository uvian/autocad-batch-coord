using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace BatchCoord
{
    public class Commands
    {
        [CommandMethod("ZDZB")]
        [CommandMethod("BZ")]
        public void BatchCoord()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            ed.WriteMessage("\n=== 自动坐标标注 ZDZB ===");

            try
            {
                // 1. 选择矩形范围（边界框）
                ed.WriteMessage("\n指定标注范围的第一角点: ");
                var pt1Res = ed.GetPoint(new PromptPointOptions("\n指定标注范围的第一个角点: "));
                if (pt1Res.Status != PromptStatus.OK) { ed.WriteMessage("\n已取消。"); return; }

                ed.WriteMessage("\n指定对角点: ");
                var pt2Res = ed.GetCorner(new PromptCornerOptions("\n指定对角点: ", pt1Res.Value));
                if (pt2Res.Status != PromptStatus.OK) { ed.WriteMessage("\n已取消。"); return; }

                var minX = Math.Min(pt1Res.Value.X, pt2Res.Value.X);
                var minY = Math.Min(pt1Res.Value.Y, pt2Res.Value.Y);
                var maxX = Math.Max(pt1Res.Value.X, pt2Res.Value.X);
                var maxY = Math.Max(pt1Res.Value.Y, pt2Res.Value.Y);
                var boundary = new Extents2d(minX, minY, maxX, maxY);

                // 2. 选择范围内的多段线
                var filter = new SelectionFilter(new[] { new TypedValue(0, "LWPOLYLINE") });
                var selectionRes = ed.SelectAll(filter);
                if (selectionRes.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n图中没有可用的多段线。");
                    return;
                }

                // 3. 小数位数
                int decimals = 3;
                var decRes = ed.GetInteger(new PromptIntegerOptions("\n小数位数 <3>: ")
                { AllowNegative = false, AllowZero = false, DefaultValue = 3, LowerLimit = 0, UpperLimit = 6 });
                if (decRes.Status == PromptStatus.OK) decimals = decRes.Value;

                // 4. 图纸比例 & 字高
                double dimScale = GetDimScale(db);
                double defaultTextH = dimScale * 2.5;
                var htRes = ed.GetDouble(new PromptDoubleOptions($"\n字高 [回车={defaultTextH:F1}]: ")
                { AllowNegative = false, AllowZero = false, DefaultValue = defaultTextH });
                double textHeight = htRes.Status == PromptStatus.OK ? htRes.Value : defaultTextH;

                // 5. 提取范围内的顶点
                var allVerts = new List<VertexInfo>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject so in selectionRes.Value)
                    {
                        var pl = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Polyline;
                        if (pl == null || !pl.Closed) continue;

                        // 检查多段线是否在矩形范围内（质心在内即可）
                        if (!IsPolylineInBoundary(pl, boundary)) continue;

                        ExtractVertices(pl, allVerts);
                    }
                    tr.Commit();
                }

                if (allVerts.Count == 0) { ed.WriteMessage("\n指定范围内未找到闭合多段线。"); return; }
                ed.WriteMessage($"\n共 {allVerts.Count} 个角点，正在计算最优标注位置...");

                // 6. 碰撞检测 + 放置
                var placedLabels = PlaceLabels(allVerts, textHeight, decimals, boundary);

                // 7. 生成标注
                int placed = 0;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    foreach (var label in placedLabels)
                    {
                        CreateCoordAnnotation(tr, ms, label, textHeight, decimals);
                        placed++;
                    }
                    tr.Commit();
                }

                ed.WriteMessage($"\n✅ 完成！共生成 {placed} 个坐标标注。");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n❌ 错误: {ex.Message}");
            }
        }

        #region 边界检测

        private bool IsPolylineInBoundary(Polyline pl, Extents2d boundary)
        {
            double cx = 0, cy = 0;
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                var pt = pl.GetPoint2dAt(i);
                cx += pt.X; cy += pt.Y;
            }
            cx /= n; cy /= n;
            return cx >= boundary.MinPoint.X && cx <= boundary.MaxPoint.X &&
                   cy >= boundary.MinPoint.Y && cy <= boundary.MaxPoint.Y;
        }

        #endregion

        #region 顶点提取与方向计算

        private class VertexInfo
        {
            public Point2d Point { get; set; }
            public Vector2d OutwardDir { get; set; }
            public Point2d Centroid { get; set; }
        }

        private void ExtractVertices(Polyline pl, List<VertexInfo> list)
        {
            double cx = 0, cy = 0;
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                var pt = pl.GetPoint2dAt(i);
                cx += pt.X; cy += pt.Y;
            }
            var centroid = new Point2d(cx / n, cy / n);

            for (int i = 0; i < n; i++)
            {
                var pt = pl.GetPoint2dAt(i);
                var prev = pl.GetPoint2dAt((i - 1 + n) % n);
                var next = pl.GetPoint2dAt((i + 1) % n);

                var v1 = (pt - prev).GetNormal();
                var v2 = (next - pt).GetNormal();
                var bisector = ((v1 + v2) * 0.5).GetNormal();
                var toCenter = (centroid - pt).GetNormal();
                double dot = bisector.X * toCenter.X + bisector.Y * toCenter.Y;

                Vector2d outward;
                if (dot > 0)
                    outward = new Vector2d(-bisector.X, -bisector.Y);
                else
                    outward = bisector;

                list.Add(new VertexInfo { Point = pt, OutwardDir = outward.GetNormal(), Centroid = centroid });
            }
        }

        #endregion

        #region 碰撞检测

        private class PlacedLabel
        {
            public Point2d Anchor { get; set; }
            public Point2d TextPos { get; set; }
            public double BoxMinX { get; set; }
            public double BoxMinY { get; set; }
            public double BoxMaxX { get; set; }
            public double BoxMaxY { get; set; }
            public bool InsidePolygon { get; set; }
        }

        private class GridCell
        {
            public List<double> Boxes { get; } = new List<double>();
            // Stores as flat array: [minX, minY, maxX, maxY, ...]
            public void Add(double minX, double minY, double maxX, double maxY)
            {
                Boxes.Add(minX); Boxes.Add(minY); Boxes.Add(maxX); Boxes.Add(maxY);
            }
        }

        private List<PlacedLabel> PlaceLabels(List<VertexInfo> verts, double textH, int decimals, Extents2d boundary)
        {
            var dirs = new[]
            {
                new Vector2d(1, 1).GetNormal(),
                new Vector2d(1, -1).GetNormal(),
                new Vector2d(-1, 1).GetNormal(),
                new Vector2d(-1, -1).GetNormal(),
                new Vector2d(1, 0).GetNormal(),
                new Vector2d(0, 1).GetNormal(),
                new Vector2d(-1, 0).GetNormal(),
                new Vector2d(0, -1).GetNormal(),
            };

            double cw = textH * 0.7;
            string sample = $"X={new string('9', 6)}";
            double lblW = sample.Length * cw;
            double lblH = textH * 2.8;
            double baseOff = lblH * 0.5;
            double cellSz = lblW * 1.5;
            var grid = new Dictionary<string, GridCell>();
            var results = new List<PlacedLabel>();

            var sorted = verts.Select((v, i) => new { v, dist = v.Point.GetDistanceTo(v.Centroid) })
                              .OrderByDescending(x => x.dist).ToList();

            foreach (var item in sorted)
            {
                var v = item.v;
                bool placed = false;
                var best = v.OutwardDir;
                var sortedDirs = dirs.Select((d, i) => new { d, i, dot = d.X * best.X + d.Y * best.Y })
                                     .OrderByDescending(x => x.dot).ToList();

                for (int dl = 0; dl < 4 && !placed; dl++)
                {
                    double off = baseOff * (1 + dl * 0.8);
                    foreach (var sd in sortedDirs)
                    {
                        var tp = new Point2d(v.Point.X + sd.d.X * off, v.Point.Y + sd.d.Y * off);

                        // 文字必须落在边界矩形内
                        if (tp.X < boundary.MinPoint.X || tp.X > boundary.MaxPoint.X ||
                            tp.Y < boundary.MinPoint.Y || tp.Y > boundary.MaxPoint.Y)
                            continue;

                        double bx = sd.d.X > 0 ? tp.X : tp.X - lblW;
                        double by = sd.d.Y > 0 ? tp.Y : tp.Y - lblH;
                        double ex = bx + lblW;
                        double ey = by + lblH;

                        if (CheckCollision(grid, bx, by, ex, ey, cellSz)) continue;
                        AddToGrid(grid, bx, by, ex, ey, cellSz);
                        results.Add(new PlacedLabel
                        {
                            Anchor = v.Point, TextPos = tp,
                            BoxMinX = bx, BoxMinY = by, BoxMaxX = ex, BoxMaxY = ey,
                            InsidePolygon = false
                        });
                        placed = true;
                        break;
                    }
                }

                if (!placed)
                {
                    var inward = (v.Centroid - v.Point).GetNormal();
                    var tp = new Point2d(v.Point.X + inward.X * lblH * 0.3, v.Point.Y + inward.Y * lblH * 0.3);
                    double bx = tp.X - lblW * 0.5, by = tp.Y - lblH * 0.5;
                    double ex = bx + lblW, ey = by + lblH;
                    AddToGrid(grid, bx, by, ex, ey, cellSz);
                    results.Add(new PlacedLabel
                    {
                        Anchor = v.Point, TextPos = tp,
                        BoxMinX = bx, BoxMinY = by, BoxMaxX = ex, BoxMaxY = ey,
                        InsidePolygon = true
                    });
                }
            }
            return results;
        }

        private bool CheckCollision(Dictionary<string, GridCell> grid,
            double bx, double by, double ex, double ey, double cs)
        {
            int x0 = (int)Math.Floor(bx / cs);
            int x1 = (int)Math.Floor(ex / cs);
            int y0 = (int)Math.Floor(by / cs);
            int y1 = (int)Math.Floor(ey / cs);
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    string key = $"{x},{y}";
                    if (!grid.ContainsKey(key)) continue;
                    var cell = grid[key];
                    var boxes = cell.Boxes;
                    for (int i = 0; i < boxes.Count; i += 4)
                    {
                        if (!(ex <= boxes[i] || boxes[i + 2] <= bx ||
                              ey <= boxes[i + 1] || boxes[i + 3] <= by))
                            return true;
                    }
                }
            }
            return false;
        }

        private void AddToGrid(Dictionary<string, GridCell> grid,
            double bx, double by, double ex, double ey, double cs)
        {
            int x0 = (int)Math.Floor(bx / cs);
            int x1 = (int)Math.Floor(ex / cs);
            int y0 = (int)Math.Floor(by / cs);
            int y1 = (int)Math.Floor(ey / cs);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    string key = $"{x},{y}";
                    if (!grid.ContainsKey(key))
                        grid[key] = new GridCell();
                    grid[key].Add(bx, by, ex, ey);
                }
        }

        #endregion

        #region 生成标注实体

        private void CreateCoordAnnotation(Transaction tr, BlockTableRecord ms,
            PlacedLabel label, double textH, int decimals)
        {
            var anchor3d = new Point3d(label.Anchor.X, label.Anchor.Y, 0);
            var textPos3d = new Point3d(label.TextPos.X, label.TextPos.Y, 0);

            // 1. 角点标记圆
            var dot = new Circle();
            dot.Center = anchor3d;
            dot.Normal = Vector3d.ZAxis;
            dot.Radius = textH * 0.08;
            dot.ColorIndex = 2;
            ms.AppendEntity(dot);
            tr.AddNewlyCreatedDBObject(dot, true);

            // 2. 引线（折线）
            if (!label.InsidePolygon)
            {
                var leader = new Polyline();
                leader.AddVertexAt(0, label.Anchor, 0, 0, 0);
                double mx = (label.Anchor.X + label.TextPos.X) * 0.5;
                double my = (label.Anchor.Y + label.TextPos.Y) * 0.5;
                leader.AddVertexAt(1, new Point2d(mx, my), 0, 0, 0);
                leader.AddVertexAt(2, label.TextPos, 0, 0, 0);
                leader.ColorIndex = 2;
                ms.AppendEntity(leader);
                tr.AddNewlyCreatedDBObject(leader, true);
            }

            // 3. 坐标文字
            string fmt = $"F{decimals}";
            string coordX = $"X={label.Anchor.X.ToString(fmt)}";
            string coordY = $"Y={label.Anchor.Y.ToString(fmt)}";
            var mtext = new MText();
            mtext.Contents = coordX + "\\P" + coordY;
            mtext.TextHeight = textH;
            mtext.Location = textPos3d;
            mtext.Attachment = AttachmentPoint.MiddleCenter;
            mtext.ColorIndex = 2;
            ms.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);
        }

        #endregion

        #region 图纸比例

        private double GetDimScale(Database db)
        {
            try
            {
                var obj = Application.GetSystemVariable("DIMSCALE");
                if (obj is double d && d > 0) return d;
            }
            catch { }
            return 100;
        }

        #endregion
    }
}
