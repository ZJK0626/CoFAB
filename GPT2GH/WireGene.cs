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

            // 存放每层轮廓到 DataTree (一层一branch)
            var contourTree = new Grasshopper.DataTree<Grasshopper.Kernel.Types.GH_Curve>();

            // 记录每层的“代表多段线”(通常是外轮廓)以便做跨层连线
            var polyPerLayer = new List<Polyline>();

            // 3. 按层切片
            int layerIndex = 0;
            for (double currentZ = zMin; currentZ <= zMax + tol; currentZ += layerHeight)
            {
                Plane slicingPlane = new Plane(new Point3d(0, 0, currentZ), Vector3d.ZAxis);

                // ---- 使用三参数签名（仅返回Curve[]），无 out intersectionPoints ----
                Curve[] intersectionCurves;
                Point3d[] intersectionPoints;
                bool rc = Intersection.BrepPlane(brep, slicingPlane, tol, out intersectionCurves, out intersectionPoints);
                if (!rc || intersectionCurves == null || intersectionCurves.Length == 0)
                    continue;
                if (intersectionCurves == null || intersectionCurves.Length == 0)
                    continue;

                // 把本层的截面曲线放进 DataTree
                var path = new Grasshopper.Kernel.Data.GH_Path(layerIndex);
                foreach (Curve c in intersectionCurves)
                {
                    contourTree.Add(new Grasshopper.Kernel.Types.GH_Curve(c), path);
                }

                // 从本层曲线中找到“外轮廓”：选取长度最大、且闭合的曲线
                double maxLen = double.NegativeInfinity;
                Curve maxCrv = null;
                foreach (Curve c in intersectionCurves)
                {
                    if (!c.IsClosed) continue; // 要求闭合
                    double len = c.GetLength();
                    if (len > maxLen)
                    {
                        maxLen = len;
                        maxCrv = c;
                    }
                }

                // 尝试把最大闭合曲线转成 Polyline（若失败就做离散）
                if (maxCrv != null)
                {
                    Polyline pl;
                    if (maxCrv.TryGetPolyline(out pl))
                    {
                        // 已经是Polyline
                        polyPerLayer.Add(pl);
                    }
                    else
                    {
                        // 手动离散
                        var divPts = DiscretizeCurve(maxCrv, sampleCount * 2);
                        var newPl = new Polyline(divPts);
                        // 闭合
                        if (!newPl.IsClosed) newPl.Add(divPts[0]);
                        polyPerLayer.Add(newPl);
                    }
                }
                else
                {
                    // 若没找到闭合线，就占个空(避免后面索引越界)
                    polyPerLayer.Add(new Polyline());
                }

                layerIndex++;
            }

            // 4. 在相邻层之间生成斜线桥接
            List<Line> bridgingLines = new List<Line>();
            for (int i = 0; i < polyPerLayer.Count - 1; i++)
            {
                var plA = polyPerLayer[i];
                var plB = polyPerLayer[i + 1];
                // 如果该层或下一层没有有效多段线，就跳过
                if (plA.Count < 2 || plB.Count < 2) continue;

                // 等分采样
                var ptsA = SamplePolyline(plA, sampleCount);
                var ptsB = SamplePolyline(plB, sampleCount);

                // 简单1对1连线
                for (int j = 0; j < sampleCount; j++)
                {
                    // 下一个索引，注意封闭多段线时要取模
                    int jNext = (j + 1) % sampleCount;

                    // 顶点索引
                    Point3d A_j = ptsA[j];
                    Point3d A_jNext = ptsA[jNext];
                    Point3d B_j = ptsB[j];
                    Point3d B_jNext = ptsB[jNext];

                    // 三角1：A_j, B_j, B_jNext
                    bridgingLines.Add(new Line(A_j, B_j));
                    bridgingLines.Add(new Line(B_j, B_jNext));
                    bridgingLines.Add(new Line(B_jNext, A_j));

                    // 三角2：A_j, A_jNext, B_jNext
                    bridgingLines.Add(new Line(A_j, A_jNext));
                    bridgingLines.Add(new Line(A_jNext, B_jNext));
                    bridgingLines.Add(new Line(B_jNext, A_j));
                }

                // 5. 输出到 GH
                DA.SetDataTree(0, contourTree);
                DA.SetDataList(1, bridgingLines);
            }
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
        /// 在给定Polyline上取样(无SegmentLength方法时，用DistanceTo逐段计算)
        /// </summary>
        private List<Point3d> SamplePolyline(Polyline pl, int sampleCount)
        {
            var result = new List<Point3d>();
            if (pl.Count < 2) return result;

            // 计算折线总长度
            double totalLen = 0.0;
            for (int i = 0; i < pl.Count - 1; i++)
            {
                totalLen += pl[i].DistanceTo(pl[i + 1]);
            }
            if (totalLen <= 0) return result;

            // 单步距离
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
                    // 超过总长时，直接用末点
                    result.Add(pl[pl.Count - 1]);
                    break;
                }

                // 查找当前段
                while (targetDist > distSoFar + segLen && segIndex < pl.Count - 2)
                {
                    distSoFar += segLen;
                    segIndex++;
                    segLen = pl[segIndex].DistanceTo(pl[segIndex + 1]);
                    segVec = pl[segIndex + 1] - pl[segIndex];
                }

                double remain = targetDist - distSoFar; // 在该段内的距离
                double t = remain / segLen; // 归一化
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