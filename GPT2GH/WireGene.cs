using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Rhino;
using Rhino.Geometry.Intersect;

namespace MyGrasshopperPlugin
{
    public class SlicingBrepComponent : GH_Component
    {
        public SlicingBrepComponent()
          : base("Slicing Brep", "Slicer",
              "Slice a Brep into contour curves",
              "GPT2GH", "Processing")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("Brep", "B", "Input Brep to be sliced", GH_ParamAccess.item);
            pManager.AddNumberParameter("LayerHeight", "LH", "Layer height for slicing", GH_ParamAccess.item, 1.0);
            pManager.AddIntegerParameter("SamplesPerLayer", "S", "Number of sample points per layer contour", GH_ParamAccess.item, 12);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Contours", "C", "Contour curves of each layer (DataTree)", GH_ParamAccess.tree);
            pManager.AddLineParameter("Bridges", "Br", "Bridging lines between consecutive layers", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // 1. 获取输入
            Brep brep = null;
            double layerHeight = 1.0;
            int sampleCount = 12;

            if (!DA.GetData(0, ref brep)) return;
            if (!DA.GetData(1, ref layerHeight)) return;
            if (!DA.GetData(2, ref sampleCount)) return;
            if (brep == null) return;
            if (layerHeight <= 0.0) return;
            if (sampleCount < 2) sampleCount = 2;

            // 2. 求 BRep 在 Z 方向的范围
            BoundingBox bbox = brep.GetBoundingBox(true);
            double zMin = bbox.Min.Z;
            double zMax = bbox.Max.Z;
            double tol = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;

            // === 关键改动：将顶层略微下移以切到完整闭合截面 ===
            double shiftTop = 0.001;    // 下移距离，可按需调整
            zMax -= shiftTop;
            // 确保不小于 zMin
            if (zMax < zMin) zMax = zMin;

            // 2.1 计算总高度，并依据层高计算切片平面的Z值（含顶部剩余处理）
            double totalHeight = zMax - zMin;
            List<double> slicingHeights = new List<double>();
            slicingHeights.Add(zMin);

            if (Math.Abs(totalHeight % layerHeight) < tol)
            {
                int numLayers = (int)Math.Round(totalHeight / layerHeight);
                for (int i = 1; i <= numLayers; i++)
                {
                    slicingHeights.Add(zMin + i * layerHeight);
                }
            }
            else
            {
                int fullLayers = (int)Math.Floor(totalHeight / layerHeight);
                double leftover = totalHeight - fullLayers * layerHeight;

                if (leftover < 0.2 * layerHeight)
                {
                    // 剩余不足0.2层高：将最后一层延长
                    for (int i = 1; i < fullLayers; i++)
                    {
                        slicingHeights.Add(zMin + i * layerHeight);
                    }
                    slicingHeights.Add(zMin + (fullLayers - 1) * layerHeight + (layerHeight + leftover));
                }
                else if (leftover >= 0.2 * layerHeight && leftover <= 0.8 * layerHeight)
                {
                    // 剩余介于0.2-0.8层高：合并为两层
                    for (int i = 1; i < fullLayers; i++)
                    {
                        slicingHeights.Add(zMin + i * layerHeight);
                    }
                    double newLayerHeight = (layerHeight + leftover) / 2.0;
                    slicingHeights.Add(slicingHeights[slicingHeights.Count - 1] + newLayerHeight);
                    slicingHeights.Add(slicingHeights[slicingHeights.Count - 1] + newLayerHeight);
                }
                else
                {
                    // 剩余接近一整层：新增一个顶层
                    for (int i = 1; i <= fullLayers; i++)
                    {
                        slicingHeights.Add(zMin + i * layerHeight);
                    }
                    slicingHeights.Add(zMin + fullLayers * layerHeight + leftover);
                }
            }

            // 3. 存放每层轮廓到 DataTree
            var contourTree = new Grasshopper.DataTree<Grasshopper.Kernel.Types.GH_Curve>();
            var polyPerLayer = new List<Polyline>();

            // 4. 按计算好的切片平面切片
            int layerIndex = 0;
            foreach (double currentZ in slicingHeights)
            {
                Plane slicingPlane = new Plane(new Point3d(0, 0, currentZ), Vector3d.ZAxis);

                bool rc = Intersection.BrepPlane(brep, slicingPlane, tol, out Curve[] intersectionCurves, out Point3d[] intersectionPoints);

                var path = new Grasshopper.Kernel.Data.GH_Path(layerIndex);

                if (!rc || intersectionCurves == null || intersectionCurves.Length == 0)
                {
                    contourTree.EnsurePath(path);
                    polyPerLayer.Add(new Polyline());
                }
                else
                {
                    foreach (Curve c in intersectionCurves)
                    {
                        contourTree.Add(new Grasshopper.Kernel.Types.GH_Curve(c), path);
                    }

                    // 找外轮廓（闭合且最长）
                    double maxLen = double.NegativeInfinity;
                    Curve maxCrv = null;
                    foreach (Curve c in intersectionCurves)
                    {
                        if (!c.IsClosed) continue;
                        double len = c.GetLength();
                        if (len > maxLen)
                        {
                            maxLen = len;
                            maxCrv = c;
                        }
                    }

                    if (maxCrv != null)
                    {
                        if (maxCrv.TryGetPolyline(out Polyline pl))
                        {
                            polyPerLayer.Add(pl);
                        }
                        else
                        {
                            var divPts = DiscretizeCurve(maxCrv, sampleCount * 2);
                            var newPl = new Polyline(divPts);
                            if (!newPl.IsClosed) newPl.Add(divPts[0]);
                            polyPerLayer.Add(newPl);
                        }
                    }
                    else
                    {
                        // 若无闭合线，就放空
                        polyPerLayer.Add(new Polyline());
                    }
                }
                layerIndex++;
            }

            // 5. 在相邻层之间生成斜线桥接
            List<Line> bridgingLines = new List<Line>();
            for (int i = 0; i < polyPerLayer.Count - 1; i++)
            {
                var plA = polyPerLayer[i];
                var plB = polyPerLayer[i + 1];
                if (plA.Count < 2 || plB.Count < 2) continue;

                var ptsA = SamplePolyline(plA, sampleCount);
                var ptsB = SamplePolyline(plB, sampleCount);

                for (int j = 0; j < sampleCount; j++)
                {
                    int jNext = (j + 1) % sampleCount;
                    Point3d A_j = ptsA[j];
                    Point3d B_j = ptsB[j];
                    Point3d B_jNext = ptsB[jNext];

                    bridgingLines.Add(new Line(A_j, B_j));
                    bridgingLines.Add(new Line(B_j, B_jNext));
                    bridgingLines.Add(new Line(B_jNext, A_j));

                    Point3d A_jNext = ptsA[jNext];
                    bridgingLines.Add(new Line(A_j, A_jNext));
                    bridgingLines.Add(new Line(A_jNext, B_jNext));
                    bridgingLines.Add(new Line(B_jNext, A_j));
                }
            }

            // 6. 输出结果
            DA.SetDataTree(0, contourTree);
            DA.SetDataList(1, bridgingLines);
        }

        // 辅助函数：将曲线等分为固定数量的点
        private List<Point3d> DiscretizeCurve(Curve crv, int count)
        {
            var pts = new List<Point3d>();
            crv.DivideByCount(count, true, out Point3d[] ptsArr);
            if (ptsArr != null)
            {
                pts.AddRange(ptsArr);
            }
            return pts;
        }

        // 辅助函数：在给定 Polyline 上取样
        private List<Point3d> SamplePolyline(Polyline pl, int sampleCount)
        {
            var result = new List<Point3d>();
            if (pl.Count < 2) return result;

            double totalLen = 0.0;
            for (int i = 0; i < pl.Count - 1; i++)
            {
                totalLen += pl[i].DistanceTo(pl[i + 1]);
            }
            if (totalLen <= 0) return result;

            double step = totalLen / (sampleCount - 1);
            int segIndex = 0;
            double distSoFar = 0.0;
            double segLen = pl[segIndex].DistanceTo(pl[segIndex + 1]);
            Vector3d segVec = pl[segIndex + 1] - pl[segIndex];

            for (int i = 0; i < sampleCount; i++)
            {
                double targetDist = i * step;
                if (targetDist >= totalLen)
                {
                    result.Add(pl[pl.Count - 1]);
                    break;
                }

                while (targetDist > distSoFar + segLen && segIndex < pl.Count - 2)
                {
                    distSoFar += segLen;
                    segIndex++;
                    segLen = pl[segIndex].DistanceTo(pl[segIndex + 1]);
                    segVec = pl[segIndex + 1] - pl[segIndex];
                }

                double remain = targetDist - distSoFar;
                double t = remain / segLen;
                Point3d pt = pl[segIndex] + segVec * t;
                result.Add(pt);
            }

            return result;
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("397e8c9e-7f56-4247-8b92-8bc8e32a1712"); }
        }

        protected override System.Drawing.Bitmap Icon
        {
            get { return null; }
        }
    }
}
