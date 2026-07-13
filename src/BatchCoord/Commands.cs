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
        public void BatchCoord()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var db = doc.Database;
            var ed = doc.Editor;

            ed.WriteMessage("\n=== 自动坐标标注 ZDZB ===\n");

            try
            {
                // ── 1. 选择闭合多段线（地块） ──
                var selOpts = new PromptSelectionOptions();
                selOpts.MessageForAdding = "\n选择要标注坐标的地块（闭合多段线）: ";
                var filter = new SelectionFilter(new[] { new TypedValue(0, "LWPOLYLINE") });
                var selRes = ed.GetSelection(selOpts, filter);
                if (selRes.Status != PromptStatus.OK) { ed.WriteMessage("已取消。\n"); return; }

                // ── 2. 矩形约束范围（标注不超出此框） ──
                ed.WriteMessage("\n指定坐标标注的约束范围 — 标注不超出此矩形。\n");
                var pt1Res = ed.GetPoint(new PromptPointOptions("\n第一角点: "));
                if (pt1Res.Status != PromptStatus.OK) { ed.WriteMessage("已取消。\n"); return; }
                var pt2Res = ed.GetCorner(new PromptCornerOptions("\n对角点: ", pt1Res.Value));
                if (pt2Res.Status != PromptStatus.OK) { ed.WriteMessage("已取消。\n"); return; }

                double bMinX = Math.Min(pt1Res.Value.X, pt2Res.Value.X);
                double bMinY = Math.Min(pt1Res.Value.Y, pt2Res.Value.Y);
                double bMaxX = Math.Max(pt1Res.Value.X, pt2Res.Value.X);
                double bMaxY = Math.Max(pt1Res.Value.Y, pt2Res.Value.Y);

                // ── 3. 小数位数 ──
                int decimals = 3;
                var decRes = ed.GetInteger(new PromptIntegerOptions("\n小数位数 <3>: ")
                { AllowNegative = false, AllowZero = false, DefaultValue = 3, LowerLimit = 0, UpperLimit = 6 });
                if (decRes.Status == PromptStatus.OK) decimals = decRes.Value;

                // ── 4. 字高 ──
                double dimScale = GetDimScale(db);
                double defaultTextH = dimScale * 2.5;
                var htRes = ed.GetDouble(new PromptDoubleOptions($"\n字高 [回车={defaultTextH:F1}]: ")
                { AllowNegative = false, AllowZero = false, DefaultValue = defaultTextH });
                double textHeight = htRes.Status == PromptStatus.OK ? htRes.Value : defaultTextH;

                // ── 5. 提取顶点 ──
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

                if (allVerts.Count == 0) { ed.WriteMessage("未选中有效的闭合多段线。\n"); return; }
                ed.WriteMessage($"共 {allVerts.Count} 个角点，正在计算最优标注位置...");

                // ── 6. 碰撞检测 + 放置 ──
                var placedLabels = PlaceLabels(allVerts, textHeight, decimals, bMinX, bMinY, bMaxX, bMaxY);

                // ── 7. 生成标注实体 ──
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

        #region 碰撞检测与放置

        private class PlacedLabel
        {
            public Point2d Anchor { get; set; }
            /// <summary>水平线中心点（引线末端位置）</summary>
            public Point2d LandingCenter { get; set; }
            /// <summary>标注整体包围盒</summary>
            public double BoxMinX { get; set; }
            public double BoxMinY { get; set; }
            public double BoxMaxX { get; set; }
            public double BoxMaxY { get; set; }
            public bool IsInsidePolygon { get; set; }
        }

        private class GridCell
        {
            public List<double> Boxes { get; } = new List<double>();
            public void Add(double minX, double minY, double maxX, double maxY)
            {
                Boxes.Add(minX); Boxes.Add(minY); Boxes.Add(maxX); Boxes.Add(maxY);
            }
        }

        private List<PlacedLabel> PlaceLabels(List<VertexInfo> verts, double textH, int decimals,
            double bMinX, double bMinY, double bMaxX, double bMaxY)
        {
            // 计算文字及水平线尺寸
            string fmt = $"F{decimals}";
            double charW = textH * 0.7;
            double pad = textH * 0.15;

            double sample = Math.Pow(10, decimals) * 2;
            string sampleX = $"X={sample.ToString(fmt)}";
            string sampleY = $"Y={sample.ToString(fmt)}";
            double lineW = Math.Max(sampleX.Length, sampleY.Length) * charW + charW * 2;
            double halfW = lineW * 0.5;
            double totalH = textH + pad + textH;   // 标注视觉高度
            double minOffset = halfW + textH * 0.8; // 最小偏移：至少半个标注宽 + 间距

            double cellSz = Math.Max(lineW, totalH) * 1.5;

            // 16 方向 + 4 次微调方向 = 20 方向
            var dirs = new Vector2d[20];
            for (int i = 0; i < 16; i++)
            {
                double ang = i * Math.PI / 8;
                dirs[i] = new Vector2d(Math.Cos(ang), Math.Sin(ang));
            }
            // 补充 4 个 22.5° 间的细方向
            for (int i = 0; i < 4; i++)
            {
                double ang = (i * 2 + 1) * Math.PI / 8;
                dirs[16 + i] = new Vector2d(Math.Cos(ang), Math.Sin(ang));
            }

            var grid = new Dictionary<string, GridCell>();
            var results = new List<PlacedLabel>();

            // 按距离形心从远到近排序
            var sorted = verts.Select(v => new { v, dist = v.Point.GetDistanceTo(v.Centroid) })
                              .OrderByDescending(x => x.dist).ToList();

            foreach (var item in sorted)
            {
                var v = item.v;
                bool placed = false;

                // 按与 OutwardDir 匹配度排序方向
                var sortedDirs = dirs.Select(d => new
                {
                    dir = d,
                    score = d.X * v.OutwardDir.X + d.Y * v.OutwardDir.Y
                }).OrderByDescending(x => x.score).ToList();

                // 试 15 层偏移，从 1×标注半宽 到 10×标注半宽
                for (int layer = 0; layer < 15 && !placed; layer++)
                {
                    double offset = minOffset + layer * halfW * 0.8;

                    foreach (var sd in sortedDirs)
                    {
                        var landing = new Point2d(
                            v.Point.X + sd.dir.X * offset,
                            v.Point.Y + sd.dir.Y * offset);

                        // 整个标注体必须在约束矩形内
                        if (landing.X - halfW < bMinX || landing.X + halfW > bMaxX ||
                            landing.Y - textH - pad < bMinY || landing.Y + textH + pad > bMaxY)
                            continue;

                        double bx = landing.X - halfW;
                        double by = landing.Y - textH - pad;
                        double ex = landing.X + halfW;
                        double ey = landing.Y + textH + pad;

                        if (CheckCollision(grid, bx, by, ex, ey, cellSz))
                            continue;

                        AddToGrid(grid, bx, by, ex, ey, cellSz);
                        results.Add(new PlacedLabel
                        {
                            Anchor = v.Point,
                            LandingCenter = landing,
                            BoxMinX = bx, BoxMinY = by, BoxMaxX = ex, BoxMaxY = ey,
                            IsInsidePolygon = false
                        });
                        placed = true;
                        break;
                    }
                }

                // 后备：多边形内部 + 矩形边缘放置
                if (!placed)
                {
                    // 在矩形四条边上均匀采点作为后备位置
                    var fallbacks = new List<Point2d>();

                    // 上边
                    for (double t = 0; t <= 1; t += 0.1)
                    {
                        double fx = bMinX + halfW + t * (bMaxX - bMinX - 2 * halfW);
                        fallbacks.Add(new Point2d(fx, bMaxY - textH - pad));
                    }
                    // 下边
                    for (double t = 0; t <= 1; t += 0.1)
                    {
                        double fx = bMinX + halfW + t * (bMaxX - bMinX - 2 * halfW);
                        fallbacks.Add(new Point2d(fx, bMinY + textH + pad));
                    }
                    // 左边
                    for (double t = 0; t <= 1; t += 0.1)
                    {
                        double fy = bMinY + textH + pad + t * (bMaxY - bMinY - 2 * (textH + pad));
                        fallbacks.Add(new Point2d(bMinX + halfW, fy));
                    }
                    // 右边
                    for (double t = 0; t <= 1; t += 0.1)
                    {
                        double fy = bMinY + textH + pad + t * (bMaxY - bMinY - 2 * (textH + pad));
                        fallbacks.Add(new Point2d(bMaxX - halfW, fy));
                    }

                    // 按离顶点的距离排序，近的优先
                    fallbacks = fallbacks.OrderBy(p => p.GetDistanceTo(v.Point)).ToList();

                    foreach (var fb in fallbacks)
                    {
                        double bx = fb.X - halfW;
                        double by = fb.Y - textH - pad;
                        double ex = fb.X + halfW;
                        double ey = fb.Y + textH + pad;

                        if (CheckCollision(grid, bx, by, ex, ey, cellSz))
                            continue;

                        AddToGrid(grid, bx, by, ex, ey, cellSz);
                        results.Add(new PlacedLabel
                        {
                            Anchor = v.Point,
                            LandingCenter = fb,
                            BoxMinX = bx, BoxMinY = by, BoxMaxX = ex, BoxMaxY = ey,
                            IsInsidePolygon = false
                        });
                        placed = true;
                        break;
                    }

                    // 实在没有位置了，强制放
                    if (!placed)
                    {
                        var center = new Point2d(
                            (bMinX + bMaxX) * 0.5,
                            (bMinY + bMaxY) * 0.5);
                        double bx = center.X - halfW;
                        double by = center.Y - textH - pad;
                        double ex = center.X + halfW;
                        double ey = center.Y + textH + pad;
                        AddToGrid(grid, bx, by, ex, ey, cellSz);
                        results.Add(new PlacedLabel
                        {
                            Anchor = v.Point,
                            LandingCenter = center,
                            BoxMinX = bx, BoxMinY = by, BoxMaxX = ex, BoxMaxY = ey,
                            IsInsidePolygon = true
                        });
                    }
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
            string fmt = $"F{decimals}";
            string coordX = $"X={label.Anchor.X.ToString(fmt)}";
            string coordY = $"Y={label.Anchor.Y.ToString(fmt)}";

            double charW = textH * 0.7;
            double lineW = Math.Max(coordX.Length, coordY.Length) * charW;
            double halfW = lineW * 0.5;
            double pad = textH * 0.15;

            var anchor3d = new Point3d(label.Anchor.X, label.Anchor.Y, 0);
            var landing3d = new Point3d(label.LandingCenter.X, label.LandingCenter.Y, 0);

            // ── 1. 测量点小圆点 ──
            var dot = new Circle
            {
                Center = anchor3d,
                Normal = Vector3d.ZAxis,
                Radius = textH * 0.08,
                ColorIndex = 2
            };
            ms.AppendEntity(dot);
            tr.AddNewlyCreatedDBObject(dot, true);

            // ── 2. 引线（直线，从锚点 → 水平线中心） ──
            // 使用 Line 而非 Polyline，结构更简单
            var leader = new Line(anchor3d, landing3d)
            {
                ColorIndex = 2
            };
            ms.AppendEntity(leader);
            tr.AddNewlyCreatedDBObject(leader, true);

            // ── 3. 水平线 ──
            var hLine = new Line(
                new Point3d(label.LandingCenter.X - halfW, label.LandingCenter.Y, 0),
                new Point3d(label.LandingCenter.X + halfW, label.LandingCenter.Y, 0))
            {
                ColorIndex = 2
            };
            ms.AppendEntity(hLine);
            tr.AddNewlyCreatedDBObject(hLine, true);

            // ── 4. X = 坐标值（水平线上方） ──
            var xMText = new MText
            {
                Contents = coordX,
                TextHeight = textH,
                Location = new Point3d(label.LandingCenter.X, label.LandingCenter.Y + pad, 0),
                Attachment = AttachmentPoint.BottomCenter,
                ColorIndex = 2
            };
            ms.AppendEntity(xMText);
            tr.AddNewlyCreatedDBObject(xMText, true);

            // ── 5. Y = 坐标值（水平线下方） ──
            var yMText = new MText
            {
                Contents = coordY,
                TextHeight = textH,
                Location = new Point3d(label.LandingCenter.X, label.LandingCenter.Y - pad, 0),
                Attachment = AttachmentPoint.TopCenter,
                ColorIndex = 2
            };
            ms.AppendEntity(yMText);
            tr.AddNewlyCreatedDBObject(yMText, true);
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
