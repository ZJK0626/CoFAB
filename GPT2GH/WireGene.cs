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
        /// <summary>
        /// 构造函数：设定组件名称、昵称、描述、所属类别/子类别
        /// </summary>
        public SlicingBrepComponent()
          : base("Slicing Brep",                  // 组件名称
                 "Slicer",                        // 在 GH 中显示的短昵称
                 "Slice a Brep into contour curves", // 组件的简要描述
                 "AI",              // 在 GH 中的一级标签
                 "Processing"                        // 在 GH 中的二级标签
                )
        {
        }

        /// <summary>
        /// 在这里定义输入参数
        /// </summary>
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 0: Brep
            pManager.AddBrepParameter("Brep", "B", "Input Brep to be sliced", GH_ParamAccess.item);

            // 1: 层高
            pManager.AddNumberParameter(
                "LayerHeight", "LH", "Layer height for slicing", GH_ParamAccess.item, 1.0);

            // 2: 采样数量(控制跨层连线的离散点数)
            pManager.AddIntegerParameter(
                "SamplesPerLayer", "S", "Number of sample points per layer contour",
                GH_ParamAccess.item, 12);
        }

        /// <summary>
        /// 注册输出参数
        /// </summary>
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 0: 各层轮廓曲线 (DataTree 形式)
            pManager.AddCurveParameter("Contours", "C",
                "Contour curves of each layer (DataTree)", GH_ParamAccess.tree);

            // 1: 跨层连线
            pManager.AddLineParameter("Bridges", "Br",
                "Bridging lines between consecutive layers", GH_ParamAccess.list);
        }

        /// <summary>
        /// 核心处理逻辑
        /// </summary>
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

            // 2.1 计算总高度，并依据层高计算切片平面的Z值（含顶部剩余处理）
            double totalHeight = zMax - zMin;
            List<double> slicingHeights = new List<double>();
            slicingHeights.Add(zMin);

            // 判断是否整除（在容差范围内）
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
                    // 最后一层高度 = layerHeight + leftover，刚好到顶
                    slicingHeights.Add(zMin + (fullLayers - 1) * layerHeight + (layerHeight + leftover));
                }
                else if (leftover >= 0.2 * layerHeight && leftover <= 0.8 * layerHeight)
                {
                    // 剩余介于0.2-0.8层高：将最后一层与剩余高度合并，平均分为两层
                    for (int i = 1; i < fullLayers; i++)
                    {
                        slicingHeights.Add(zMin + i * layerHeight);
                    }
                    double newLayerHeight = (layerHeight + leftover) / 2.0;
                    // 分成两层
                    slicingHeights.Add(slicingHeights[slicingHeights.Count - 1] + newLayerHeight);
                    slicingHeights.Add(slicingHeights[slicingHeights.Count - 1] + newLayerHeight);
                }
                else // leftover > 0.8 * layerHeight
                {
                    // 剩余高度接近一整层：增加一个独立的顶层
                    for (int i = 1; i <= fullLayers; i++)
                    {
                        slicingHeights.Add(zMin + i * layerHeight);
                    }
                    slicingHeights.Add(zMax);
                }
            }

            // 3. 存放每层轮廓到 DataTree (一层一 branch)
            var contourTree = new Grasshopper.DataTree<Grasshopper.Kernel.Types.GH_Curve>();

            // 记录每层的“代表多段线”（通常是外轮廓）以便做跨层连线
            var polyPerLayer = new List<Polyline>();

            // 4. 按计算好的切片平面切片
            int layerIndex = 0;
            foreach (double currentZ in slicingHeights)
            {
                Plane slicingPlane = new Plane(new Point3d(0, 0, currentZ), Vector3d.ZAxis);

                // 使用 Intersection.BrepPlane 求交，返回Curve[]
                Curve[] intersectionCurves;
                Point3d[] intersectionPoints;
                bool rc = Intersection.BrepPlane(brep, slicingPlane, tol, out intersectionCurves, out intersectionPoints);
                if (!rc || intersectionCurves == null || intersectionCurves.Length == 0)
                    continue;

                // 把本层的截面曲线放进 DataTree
                var path = new Grasshopper.Kernel.Data.GH_Path(layerIndex);
                foreach (Curve c in intersectionCurves)
                {
                    contourTree.Add(new Grasshopper.Kernel.Types.GH_Curve(c), path);
                }

                // 从本层曲线中找到“外轮廓”：选取长度最大且闭合的曲线
                double maxLen = double.NegativeInfinity;
                Curve maxCrv = null;
                foreach (Curve c in intersectionCurves)
                {
                    if (!c.IsClosed) continue; // 需要闭合曲线
                    double len = c.GetLength();
                    if (len > maxLen)
                    {
                        maxLen = len;
                        maxCrv = c;
                    }
                }

                // 尝试将最大闭合曲线转换为 Polyline（如果失败则离散取点）
                if (maxCrv != null)
                {
                    Polyline pl;
                    if (maxCrv.TryGetPolyline(out pl))
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
                    // 如果未找到闭合线，为避免后续索引出错，加入一个空的 Polyline
                    polyPerLayer.Add(new Polyline());
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

                // 等分采样
                var ptsA = SamplePolyline(plA, sampleCount);
                var ptsB = SamplePolyline(plB, sampleCount);

                // 简单1对1连线构成桥接
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

        /// <summary>
        /// 将曲线等分为固定数量的点
        /// </summary>
        private List<Point3d> DiscretizeCurve(Curve crv, int count)
        {
            var pts = new List<Point3d>();
            Point3d[] ptsArr;
            crv.DivideByCount(count, true, out ptsArr);
            if (ptsArr != null)
            {
                pts.AddRange(ptsArr);
            }
            return pts;
        }

        /// <summary>
        /// 在给定 Polyline 上取样（没有 SegmentLength 方法时，逐段计算距离）
        /// </summary>
        private List<Point3d> SamplePolyline(Polyline pl, int sampleCount)
        {
            var result = new List<Point3d>();
            if (pl.Count < 2) return result;

            // 计算总长度
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
