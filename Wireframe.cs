using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace GPT2GH
{
    public class NearConstantWireframe : GH_Component
    {
        public NearConstantWireframe()
          : base("NearConstant Wireframe",
                 "NCWire",
                 "Generate near-constant density wireframe (contours + pillars) for 3D printing",
                 "GPT2GH", "WireFrame")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddMeshParameter("Mesh", "M", "Input mesh (same as from FieldGenerator)", GH_ParamAccess.item);
            pManager.AddNumberParameter("FieldValues", "F", "Per-vertex scalar values from the field generator", GH_ParamAccess.list);
            pManager.AddNumberParameter("SliceHeight", "hS", "Target slice spacing (for iso-contour)", GH_ParamAccess.item, 0.5);
            pManager.AddNumberParameter("MinDistance", "mD", "Minimal distance among points on same contour", GH_ParamAccess.item, 1.0);
            pManager.AddNumberParameter("PillarMaxAngle", "pA", "Max angle from vertical allowed for pillar lines (deg)", GH_ParamAccess.item, 30.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 1) 每层等值线
            pManager.AddCurveParameter("LayerContours", "LC", "Contour curves by each iso slice (DataTree)", GH_ParamAccess.tree);
            // 2) 相邻层之间的支柱线
            pManager.AddLineParameter("Pillars", "PL", "3D lines bridging adjacent contours", GH_ParamAccess.list);
            // 3) 合并后的全部线段 (可进一步输出G-Code)
            pManager.AddLineParameter("AllEdges", "AE", "All wireframe edges", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Mesh mesh = null;
            List<double> fieldValues = new List<double>();
            double sliceHeight = 0.5;
            double minDist = 1.0;
            double pillarMaxAngleDeg = 30.0;

            if (!DA.GetData(0, ref mesh)) return;
            if (!DA.GetDataList(1, fieldValues)) return;
            if (!DA.GetData(2, ref sliceHeight)) return;
            if (!DA.GetData(3, ref minDist)) return;
            if (!DA.GetData(4, ref pillarMaxAngleDeg)) return;
            if (mesh == null) return;
            if (fieldValues.Count != mesh.Vertices.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "FieldValues count must match mesh vertex count.");
                return;
            }
            if (sliceHeight <= 0) sliceHeight = 0.5;

            // 0) 简化：把 field 映射到 [0, maxVal]
            double maxVal = 0.0;
            foreach (var val in fieldValues)
                if (val > maxVal) maxVal = val;

            if (maxVal < 1e-8)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Field is near zero, nothing to slice.");
                return;
            }

            // 1) 计算 iso-values
            List<double> isoLevels = new List<double>();
            double iso = 0.0;
            while (iso <= maxVal)
            {
                isoLevels.Add(iso);
                iso += sliceHeight;
            }
            // 若最后和 maxVal 差很多，可以补一个
            if ((iso - sliceHeight) < (maxVal - 0.01)) isoLevels.Add(maxVal);

            // 2) 每个 iso-level 提取“轮廓” （实际上是Mesh内插）
            //    这里为了演示, 我们不做三角面内细致切分, 而是纯粹以插值顶点, 再连边
            //    更严谨的做法需遍历 Face, 逐一找插值线
            //    这里仅做演示，可能在鞍点处出现断层
            var layerContours = new Grasshopper.DataTree<Rhino.Geometry.Curve>();
            List<List<Point3d>> polylinesPerLayer = new List<List<Point3d>>();

            // 构建拓扑 => 每条边(vidA,vidB), 及对应 fA,fB
            MeshTopologyEdgeList edges = mesh.TopologyEdges;
            int vCount = mesh.Vertices.Count;

            // 先把所有顶点坐标缓存
            Point3d[] pts = mesh.Vertices.ToPoint3dArray();

            // “鞍点修正”演示： 若某个顶点在 f 上是 saddle, 可以略微调高 f(v) => 这里仅做简化
            int saddleCount = 0;
            for (int i = 0; i < vCount; i++)
            {
                // 判断是否鞍点(极简做法：看邻居中 f>f(i) 和 f<f(i) 是否都至少各2个)
                double fv = fieldValues[i];
                var connected = mesh.TopologyVertices.ConnectedTopologyVertices(mesh.TopologyVertices.TopologyVertexIndex(i));
                int higher = 0, lower = 0;
                for (int j = 0; j < connected.Length; j++)
                {
                    int vj = connected[j];
                    double fN = fieldValues[vj];
                    if (fN > fv) higher++;
                    else if (fN < fv) lower++;
                }
                // 若 higher>1 且 lower>1 => 认为是鞍点
                if (higher > 1 && lower > 1)
                {
                    saddleCount++;
                    // 把它往上推一点
                    fieldValues[i] = fv + 0.0001;
                }
            }
            // 这只是演示：现实中要做更精细的 saddle 等值线插值

            int layerIndex = 0;
            foreach (double level in isoLevels)
            {
                // 在每次 slicing 里，找到 edges 跨过此 isoValue 的点
                // 并将这些点连接起来 => polyline
                List<Point3d> contourPts = new List<Point3d>();
                Dictionary<IndexPair, Point3d> edgeCrossCache = new Dictionary<IndexPair, Point3d>();

                for (int eI = 0; eI < edges.Count; eI++)
                {
                    IndexPair ip = edges.GetTopologyVertices(eI);
                    // 物理顶点列表(可能>2个)
                    int[] actualVertsA = mesh.TopologyVertices.MeshVertexIndices(ip.I);
                    int[] actualVertsB = mesh.TopologyVertices.MeshVertexIndices(ip.J);
                    if (actualVertsA.Length < 1 || actualVertsB.Length < 1) continue;

                    // 先拿第一个物理顶点
                    int idxA = actualVertsA[0];
                    int idxB = actualVertsB[0];

                    double fA = fieldValues[idxA];
                    double fB = fieldValues[idxB];

                    if ((fA < level && fB > level) || (fA > level && fB < level))
                    {
                        // 在此 edge 内部插值得到交点
                        double t = (level - fA) / (fB - fA);
                        if (t < 0) t = 0; if (t > 1) t = 1;
                        Point3d pA = pts[idxA];
                        Point3d pB = pts[idxB];
                        Point3d crossPt = pA + t * (pB - pA);
                        contourPts.Add(crossPt);
                    }
                }

                // 对 contourPts 做简化：点之间距离要 >= minDist
                List<Point3d> finalContour = SimplifyPoints(contourPts, minDist);
                polylinesPerLayer.Add(finalContour);

                // 转为 GH Curve 输出
                if (finalContour.Count >= 2)
                {
                    Polyline pl = new Polyline(finalContour);
                    // 若想闭合，可判定一下距离, 是否做pl.Add(pl[0]) 
                    var path = new Grasshopper.Kernel.Data.GH_Path(layerIndex);
                    layerContours.Add(new PolylineCurve(pl), path);
                }
                layerIndex++;
            }

            // 3) 生成相邻层之间的 Pillar 线段
            //    假设每条层 contour 里点的顺序是 finalContour, 逐个匹配
            List<Line> pillarLines = new List<Line>();
            List<Line> allEdges = new List<Line>();

            double maxAngleRad = RhinoMath.ToRadians(pillarMaxAngleDeg);

            for (int i = 0; i < polylinesPerLayer.Count - 1; i++)
            {
                var layerA = polylinesPerLayer[i];
                var layerB = polylinesPerLayer[i + 1];
                if (layerA.Count < 1 || layerB.Count < 1) continue;

                // 简单做：两层之间按最近点匹配
                // 实际可用二分图最大匹配/最小距离分配
                // 这里演示把数量较少的一层当基准
                if (layerB.Count < layerA.Count)
                {
                    var tmp = layerB;
                    layerB = layerA;
                    layerA = tmp;
                }

                for (int iA = 0; iA < layerA.Count; iA++)
                {
                    Point3d pA = layerA[iA];
                    int nearestIndexB = -1;
                    double minSqr = double.MaxValue;
                    for (int iB = 0; iB < layerB.Count; iB++)
                    {
                        double dsq = pA.DistanceToSquared(layerB[iB]);
                        if (dsq < minSqr)
                        {
                            minSqr = dsq;
                            nearestIndexB = iB;
                        }
                    }
                    if (nearestIndexB < 0) continue;
                    Point3d pB = layerB[nearestIndexB];

                    // 判断夹角
                    Vector3d vec = pB - pA;
                    double angle = Vector3d.VectorAngle(vec, Vector3d.ZAxis);
                    if (angle <= maxAngleRad)
                    {
                        var line = new Line(pA, pB);
                        pillarLines.Add(line);
                    }
                }
            }

            // 4) 整理全部线段
            //    - 每层轮廓的相邻点
            //    - 以及前面找到的 pillar
            foreach (var cList in polylinesPerLayer)
            {
                for (int i = 0; i < cList.Count - 1; i++)
                {
                    allEdges.Add(new Line(cList[i], cList[i + 1]));
                }
            }
            allEdges.AddRange(pillarLines);

            // 5) 输出
            DA.SetDataTree(0, layerContours);       // DataTree of contours
            DA.SetDataList(1, pillarLines);         // pillars
            DA.SetDataList(2, allEdges);            // all edges merged
        }

        /// <summary>
        /// 将一堆 points 简化：保证两两点距离>=minDist，且尽量保留分布。
        /// 这里用极简的“依次选点 -> 剔除近点”方法（greedy）。可换更高级算法。
        /// </summary>
        private List<Point3d> SimplifyPoints(List<Point3d> pts, double minDist)
        {
            List<Point3d> result = new List<Point3d>();
            // 简单打乱or不打乱都行
            // 这里不排序了
            foreach (var p in pts)
            {
                bool keep = true;
                foreach (var q in result)
                {
                    if (p.DistanceTo(q) < minDist)
                    {
                        keep = false;
                        break;
                    }
                }
                if (keep) result.Add(p);
            }
            return result;
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("aad6e20b-bb32-421f-aeaf-ae9a24c7b9d9"); }
        }

        protected override System.Drawing.Bitmap Icon
        {
            get { return null; }
        }
    }
}
