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
        [CommandMethod("BATCHCOORD")]
        [CommandMethod("BZ")]
        public void BatchCoord()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            ed.WriteMessage("\n=== 批量坐标标注 BATCHCOORD ===");

            try
            {
                // 1. 选择多段线
                var selOpts = new PromptSelectionOptions();
                selOpts.MessageForAdding = "\n选择要标注坐标的地块（闭合多段线）: ";
                var filter = new SelectionFilter(new[] { new TypedValue(0, "LWPOLYLINE") });
                var selRes = ed.GetSelection(selOpts, filter);
                if (selRes.Status != PromptStatus.OK) { ed.WriteMessage("\n已取消。"); return; }

                // 2. 小数位数
                int decimals = 3;
                var decRes = ed.GetInteger(new PromptIntegerOptions("\n小数位数 <3>: ")
                { AllowNegative = false, AllowZero = false, DefaultValue = 3, LowerLimit = 0, UpperLimit = 6 });
                if (decRes.Status == PromptStatus.OK) decimals = decRes.Value;

                // 3. 图纸比例 & 字高
                double dimScale = GetDimScale(db);
                double defaultTextH = dimScale * 2.5;
                var htRes = ed.GetDouble(new PromptDoubleOptions($"\n字高 [回车={defaultTextH:F1}]: ")
                { AllowNegative = false, AllowZero = false, DefaultValue = defaultTextH });
                double textHeight = htRes.Status == PromptStatus.OK ? htRes.Value : defaultTextH;

                // 4. 提取顶点
                var allVerts = new List<VertexInfo>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject so in selRes.Value)
                    {
                        var pl = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Polyline;
                        if (pl == null || !pl.Closed) continue;
                        ExtractVertices(pl, allVerts);
                    }
                    tr.Commit();
                }

                if (allVerts.Count == 0) { ed.WriteMessage("\n未找到有效的闭合多段线。"); return; }
                ed.WriteMessage($"\n共 {allVerts.Count} 个角点，正在计算最优标注位置...");

                // 5. 碰撞检测 + 放置
                var placedLabels = PlaceLabels(allVerts, textHeight, decimals);

                // 6. 生成标注
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
            public Extents2d BBox { get; set; }
            public bool InsidePolygon { get; set; }
        }

        private List<PlacedLabel> PlaceLabels(List<VertexInfo> verts, double textH, int decimals)
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
            var grid = new Dictionary<(int, int), List<Extents2d>>();
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
                        double bx = sd.d.X > 0 ? tp.X : tp.X - lblW;
                        double by = sd.d.Y > 0 ? tp.Y : tp.Y - lblH;
                        var bbox = new Extents2d(bx, by, bx + lblW, by + lblH);
                        if (CheckCollision(grid, bbox, cellSz)) continue;
                        AddToGrid(grid, bbox, cellSz);
                        results.Add(new PlacedLabel { Anchor = v.Point, TextPos = tp, BBox = bbox, InsidePolygon = false });
                        placed = true;
                        break;
                    }
                }

                if (!placed)
                {
                    var inward = (v.Centroid - v.Point).GetNormal();
                    var tp = new Point2d(v.Point.X + inward.X * lblH * 0.3, v.Point.Y + inward.Y * lblH * 0.3);
                    var bbox = new Extents2d(tp.X - lblW * 0.5, tp.Y - lblH * 0.5, tp.X + lblW * 0.5, tp.Y + lblH * 0.5);
                    AddToGrid(grid, bbox, cellSz);
                    results.Add(new PlacedLabel { Anchor = v.Point, TextPos = tp, BBox = bbox, InsidePolygon = true });
                }
            }
            return results;
        }

        private bool CheckCollision(Dictionary<(int, int), List<Extents2d>> grid, Extents2d bbox, double cs)
        {
            int x0 = (int)Math.Floor(bbox.MinPoint.X / cs);
            int x1 = (int)Math.Floor(bbox.MaxPoint.X / cs);
            int y0 = (int)Math.Floor(bbox.MinPoint.Y / cs);
            int y1 = (int)Math.Floor(bbox.MaxPoint.Y / cs);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    if (grid.ContainsKey((x, y)))
                        foreach (var e in grid[(x, y)])
                            if (Overlaps(bbox, e)) return true;
            return false;
        }

        private bool Overlaps(Extents2d a, Extents2d b)
        {
            return !(a.MaxPoint.X <= b.MinPoint.X || b.MaxPoint.X <= a.MinPoint.X ||
                     a.MaxPoint.Y <= b.MinPoint.Y || b.MaxPoint.Y <= a.MinPoint.Y);
        }

        private void AddToGrid(Dictionary<(int, int), List<Extents2d>> grid, Extents2d bbox, double cs)
        {
            int x0 = (int)Math.Floor(bbox.MinPoint.X / cs);
            int x1 = (int)Math.Floor(bbox.MaxPoint.X / cs);
            int y0 = (int)Math.Floor(bbox.MinPoint.Y / cs);
            int y1 = (int)Math.Floor(bbox.MaxPoint.Y / cs);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    var k = (x, y);
                    if (!grid.ContainsKey(k)) grid[k] = new List<Extents2d>();
                    grid[k].Add(bbox);
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
            var dot = new Circle(anchor3d, Vector3d.ZAxis, textH * 0.08);
            dot.ColorIndex = 2;
            ms.AppendEntity(dot);
            tr.AddNewlyCreatedDBObject(dot, true);

            // 2. 引线
            if (!label.InsidePolygon)
            {
                var leader = new Polyline();
                leader.AddVertexAt(0, label.Anchor, 0, 0, 0);
                var mid = new Point2d((label.Anchor.X + label.TextPos.X) * 0.5,
                                      (label.Anchor.Y + label.TextPos.Y) * 0.5);
                leader.AddVertexAt(1, mid, 0, 0, 0);
                leader.AddVertexAt(2, label.TextPos, 0, 0, 0);
                leader.ColorIndex = 2;
                ms.AppendEntity(leader);
                tr.AddNewlyCreatedDBObject(leader, true);
            }

            // 3. 坐标文字
            string fmt = $"F{decimals}";
            string coordX = $"X={label.Anchor.X.ToString(fmt)}";
            string coordY = $"Y={label.Anchor.Y.ToString(fmt)}";
            var mtext = new MText
            {
                Contents = $"{coordX}\\P{coordY}",
                TextHeight = textH,
                Location = textPos3d,
                Attachment = AttachmentPoint.MiddleCenter,
                ColorIndex = 2
            };
            mtext.SetDatabaseDefaults();
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
