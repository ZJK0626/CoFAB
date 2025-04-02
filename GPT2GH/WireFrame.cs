using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace GPT2GH
{
    public class SplitTrianglesSubdivisionComponent : GH_Component
    {
        /// <summary>
        /// 构造函数：设定组件名称、昵称、描述、所属类别/子类别
        /// </summary>
        public SplitTrianglesSubdivisionComponent()
          : base("Split Triangles Subdivision",    // 组件名称
                 "SplitTri",                       // GH 中的简短昵称
                 "Subdivide mesh triangles into smaller triangles by splitting edges at midpoints.",
                 "GPT2GH",                         // GH 一级标签
                 "MeshProcessing"                  // GH 二级标签
                )
        {
        }

        /// <summary>
        /// 在这里定义输入参数
        /// </summary>
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 0: 输入网格
            pManager.AddMeshParameter(
                "Mesh", "M",
                "Mesh to subdivide (will automatically triangulate quads).",
                GH_ParamAccess.item
            );

            // 1: 细分层数
            pManager.AddIntegerParameter(
                "SubdivisionLevels", "Level",
                "Number of subdivision iterations to apply. 0 means no subdivision, 1 means one round, etc.",
                GH_ParamAccess.item,
                0 // 默认只细分一次
            );
        }

        /// <summary>
        /// 注册输出参数
        /// </summary>
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 0: 细分后的网格
            pManager.AddMeshParameter(
                "SubdividedMesh", "R",
                "Resulting subdivided mesh",
                GH_ParamAccess.item
            );
        }

        /// <summary>
        /// 核心处理逻辑
        /// </summary>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // 1. 获取输入网格
            Mesh inputMesh = null;
            if (!DA.GetData(0, ref inputMesh)) return;

            // 如果网格是 null 或无效，直接退出
            if (inputMesh == null || !inputMesh.IsValid)
            {
                this.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Input Mesh is invalid.");
                return;
            }

            // 2. 获取细分层数
            int level = 1;
            if (!DA.GetData(1, ref level)) return;

            // 确保层数非负
            if (level < 0) level = 0;

            // 限制上限为 3，防止过度细分卡死
            if (level > 3)
            {
                level = 3;
                this.AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Subdivision level exceeded maximum of 3. Clamped to 3."
                );
            }

            // 3. 将网格中的四边面转为三角面，确保后续细分时都是三角面
            inputMesh.Faces.ConvertQuadsToTriangles();

            // 4. 循环细分
            Mesh subdivided = inputMesh;
            for (int i = 0; i < level; i++)
            {
                subdivided = SplitTriangles(subdivided);
            }

            // 5. 输出结果
            DA.SetData(0, subdivided);
        }

        /// <summary>
        /// 将网格中的三角面拆分为更小的三角形。
        /// 每个三角面会细分成 4 个更小的三角面。
        /// </summary>
        private Mesh SplitTriangles(Mesh mesh)
        {
            // 获取原始顶点和面
            var oldVertices = mesh.Vertices.ToPoint3dArray();
            var oldFaces = new List<MeshFace>();
            for (int i = 0; i < mesh.Faces.Count; i++)
                oldFaces.Add(mesh.Faces[i]);

            // 新网格：用于存放细分后结果
            Mesh newMesh = new Mesh();

            // 先将所有旧顶点复制到 newMesh
            foreach (Point3d pt in oldVertices)
                newMesh.Vertices.Add(pt);

            // 字典缓存：边 -> 中点顶点索引
            Dictionary<Tuple<int, int>, int> midpointDict = new Dictionary<Tuple<int, int>, int>();

            // 辅助函数：获取或创建一条边 (i1, i2) 的中点顶点索引
            int GetMidpointIndex(int i1, int i2)
            {
                // 确保顺序一致 (小索引, 大索引)
                int minI = Math.Min(i1, i2);
                int maxI = Math.Max(i1, i2);
                var key = Tuple.Create(minI, maxI);

                if (midpointDict.ContainsKey(key))
                {
                    return midpointDict[key];
                }
                else
                {
                    Point3d p1 = oldVertices[minI];
                    Point3d p2 = oldVertices[maxI];
                    Point3d mid = 0.5 * (p1 + p2);

                    int newIndex = newMesh.Vertices.Add(mid);
                    midpointDict[key] = newIndex;

                    return newIndex;
                }
            }

            // 遍历原网格面（假设全部是三角面）
            foreach (var face in oldFaces)
            {
                // 如果不是三角面，可选择跳过或自行处理
                if (!face.IsTriangle) continue;

                int iA = face.A;
                int iB = face.B;
                int iC = face.C;

                // 计算三条边的中点索引
                int iAB = GetMidpointIndex(iA, iB);
                int iBC = GetMidpointIndex(iB, iC);
                int iCA = GetMidpointIndex(iC, iA);

                // 生成 4 个更小的三角面
                newMesh.Faces.AddFace(iA, iAB, iCA);
                newMesh.Faces.AddFace(iB, iBC, iAB);
                newMesh.Faces.AddFace(iC, iCA, iBC);
                newMesh.Faces.AddFace(iAB, iBC, iCA);
            }

            // 更新法线、压缩存储
            newMesh.Normals.ComputeNormals();
            newMesh.Compact();

            return newMesh;
        }

        /// <summary>
        /// 必须为组件生成唯一的GUID
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("16BE212F-0128-4A96-BDB6-8898E77F2B2B"); }
        }

        /// <summary>
        /// 如果有自定义图标，可在此返回；暂时返回 null
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get { return null; }
        }
    }
}
