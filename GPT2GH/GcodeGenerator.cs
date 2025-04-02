using Grasshopper.Kernel;
using Rhino.Geometry.Collections;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GPT2GH
{
    public class MeshEdgeGCodeGenerator : GH_Component
    {
        public MeshEdgeGCodeGenerator()
            : base("Mesh Edge GCode Generator", "MeshGCode",
                "Generate G-code from mesh edge",
                "GPT2GH", "MeshProcessing")
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddMeshParameter("Mesh", "M", "输入网格模型", GH_ParamAccess.item);
            pManager.AddNumberParameter("Layer Height", "LH", "每层的高度(mm)", GH_ParamAccess.item, 0.2);
            pManager.AddNumberParameter("Print Speed", "PS", "打印速度(mm/min)", GH_ParamAccess.item, 1200);
            pManager.AddNumberParameter("Travel Speed", "TS", "移动速度(mm/min)", GH_ParamAccess.item, 3000);
            pManager.AddTextParameter("Output Path", "OP", "G代码输出路径", GH_ParamAccess.item, "C:\\temp\\output.gcode");
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddLineParameter("Edges", "E", "提取的网格边缘", GH_ParamAccess.list);
            pManager.AddCurveParameter("Print Path", "PP", "优化后的打印路径", GH_ParamAccess.list);
            pManager.AddTextParameter("GCode", "G", "生成的G代码", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // 输入参数
            Mesh mesh = new Mesh();
            double layerHeight = 0.2;
            double printSpeed = 1200;
            double travelSpeed = 3000;
            string outputPath = "C:\\temp\\output.gcode";

            if (!DA.GetData(0, ref mesh)) return;
            DA.GetData(1, ref layerHeight);
            DA.GetData(2, ref printSpeed);
            DA.GetData(3, ref travelSpeed);
            DA.GetData(4, ref outputPath);

            // 确保网格是有效的
            if (!mesh.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "提供的网格无效");
                return;
            }

            // 提取网格边缘
            List<Line> edges = ExtractMeshEdges(mesh);

            // 按高度将边缘分层
            Dictionary<double, List<Line>> layeredEdges = SortEdgesByLayers(edges, layerHeight);

            // 优化每层的打印路径
            List<Curve> printPaths = new List<Curve>();
            foreach (var layer in layeredEdges.OrderBy(kvp => kvp.Key))
            {
                List<Curve> layerPaths = OptimizeLayerPrintPath(layer.Value);
                printPaths.AddRange(layerPaths);
            }

            // 生成G代码
            string gcode = GenerateGCode(printPaths, printSpeed, travelSpeed, layerHeight);

            // 保存G代码到文件
            if (!string.IsNullOrEmpty(outputPath))
            {
                try
                {
                    File.WriteAllText(outputPath, gcode);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "保存G代码失败: " + ex.Message);
                }
            }

            // 输出结果
            DA.SetDataList(0, edges);
            DA.SetDataList(1, printPaths);
            DA.SetData(2, gcode);
        }

        private List<Line> ExtractMeshEdges(Mesh mesh)
        {
            // 创建一个唯一的边缘列表
            List<Line> edges = new List<Line>();

            // 获取网格拓扑边缘
            mesh.Unweld(0, true);
            MeshTopologyEdgeList topologyEdges = mesh.TopologyEdges;

            for (int i = 0; i < topologyEdges.Count; i++)
            {
                // 直接获取边缘线
                Line edge = topologyEdges.EdgeLine(i);

                // 添加边缘线
                edges.Add(edge);
            }

            return edges;
        }

        private Dictionary<double, List<Line>> SortEdgesByLayers(List<Line> edges, double layerHeight)
        {
            Dictionary<double, List<Line>> layeredEdges = new Dictionary<double, List<Line>>();

            // 计算高度范围
            double minZ = edges.Min(e => Math.Min(e.From.Z, e.To.Z));
            double maxZ = edges.Max(e => Math.Max(e.From.Z, e.To.Z));

            // 计算层数
            int layerCount = (int)Math.Ceiling((maxZ - minZ) / layerHeight);

            // 为每一层初始化空列表
            for (int i = 0; i < layerCount; i++)
            {
                double layerZ = minZ + i * layerHeight;
                layeredEdges[layerZ] = new List<Line>();
            }

            // 将边缘分配到最近的层
            foreach (Line edge in edges)
            {
                double edgeMinZ = Math.Min(edge.From.Z, edge.To.Z);
                double edgeMaxZ = Math.Max(edge.From.Z, edge.To.Z);

                // 确定所属的层
                double layerZ = minZ + Math.Floor((edgeMinZ - minZ) / layerHeight) * layerHeight;

                // 如果边缘跨越多个层，可能需要分割它
                // 这个简单实现只将它分配给最低的层
                if (layeredEdges.ContainsKey(layerZ))
                {
                    layeredEdges[layerZ].Add(edge);
                }
            }

            return layeredEdges;
        }

        private List<Curve> OptimizeLayerPrintPath(List<Line> layerEdges)
        {
            List<Curve> optimizedPaths = new List<Curve>();

            if (layerEdges.Count == 0)
                return optimizedPaths;

            // 首先按方向分组
            var horizontalEdges = new List<Line>();
            var verticalEdges = new List<Line>();
            var diagonalEdges = new List<Line>();

            foreach (Line edge in layerEdges)
            {
                Vector3d direction = edge.Direction;
                direction.Unitize();

                // 判断方向（简化的判断）
                if (Math.Abs(direction.X) > Math.Abs(direction.Y) && Math.Abs(direction.X) > Math.Abs(direction.Z))
                {
                    horizontalEdges.Add(edge);
                }
                else if (Math.Abs(direction.Z) > Math.Abs(direction.X) && Math.Abs(direction.Z) > Math.Abs(direction.Y))
                {
                    verticalEdges.Add(edge);
                }
                else
                {
                    diagonalEdges.Add(edge);
                }
            }

            // 按照优先级顺序处理各组边缘
            List<Line> orderedEdges = new List<Line>();
            orderedEdges.AddRange(horizontalEdges);
            orderedEdges.AddRange(verticalEdges);
            orderedEdges.AddRange(diagonalEdges);

            // 尝试连接边缘以减少断开
            Polyline printPath = ConnectEdges(orderedEdges);

            // 将连接的路径转换为曲线并添加到结果
            optimizedPaths.Add(printPath.ToNurbsCurve());

            return optimizedPaths;
        }

        private Polyline ConnectEdges(List<Line> edges)
        {
            Polyline path = new Polyline();

            if (edges.Count == 0)
                return path;

            // 以第一条边开始
            Line currentEdge = edges[0];
            path.Add(currentEdge.From);
            path.Add(currentEdge.To);

            List<Line> remainingEdges = new List<Line>(edges);
            remainingEdges.RemoveAt(0);

            // 贪心算法：每次寻找距离当前点最近的边
            while (remainingEdges.Count > 0)
            {
                Point3d currentPoint = path[path.Count - 1];

                // 找到最近的边
                int closestEdgeIndex = -1;
                double minDistance = double.MaxValue;
                bool connectToStart = true;

                for (int i = 0; i < remainingEdges.Count; i++)
                {
                    double distToStart = currentPoint.DistanceTo(remainingEdges[i].From);
                    double distToEnd = currentPoint.DistanceTo(remainingEdges[i].To);

                    if (distToStart < minDistance)
                    {
                        minDistance = distToStart;
                        closestEdgeIndex = i;
                        connectToStart = true;
                    }

                    if (distToEnd < minDistance)
                    {
                        minDistance = distToEnd;
                        closestEdgeIndex = i;
                        connectToStart = false;
                    }
                }

                // 添加最近的边
                if (closestEdgeIndex >= 0)
                {
                    Line closest = remainingEdges[closestEdgeIndex];

                    // 根据连接点确定方向
                    if (connectToStart)
                    {
                        path.Add(closest.From);
                        path.Add(closest.To);
                    }
                    else
                    {
                        path.Add(closest.To);
                        path.Add(closest.From);
                    }

                    remainingEdges.RemoveAt(closestEdgeIndex);
                }
                else
                {
                    // 没有找到可连接的边，这种情况不应该发生
                    break;
                }
            }

            return path;
        }

        private string GenerateGCode(List<Curve> printPaths, double printSpeed, double travelSpeed, double layerHeight)
        {
            StringWriter gcode = new StringWriter();

            // 添加G代码头部
            gcode.WriteLine("; Generated by Mesh Edge GCode Generator");
            gcode.WriteLine("; " + DateTime.Now.ToString());
            gcode.WriteLine("G21 ; 设置为毫米单位");
            gcode.WriteLine("G90 ; 使用绝对坐标");
            gcode.WriteLine("M82 ; 使用绝对挤出");
            gcode.WriteLine("G28 ; 回零所有轴");
            gcode.WriteLine("M104 S200 ; 设置热端温度");
            gcode.WriteLine("M109 S200 ; 等待热端温度");
            gcode.WriteLine("G92 E0 ; 重置挤出机位置");
            gcode.WriteLine("G1 F" + travelSpeed + " ; 设置移动速度");

            // 跟踪当前位置和挤出量
            Point3d currentPosition = new Point3d(0, 0, 0);
            double extrusionAmount = 0;
            int currentLayer = -1;

            // 为每个路径生成G代码
            foreach (Curve path in printPaths)
            {
                // 对曲线进行离散化以获取点
                Polyline polyline = new Polyline();
                path.TryGetPolyline(out polyline);

                if (polyline == null || polyline.Count < 2)
                    continue;

                // 检查是否进入新层
                int pathLayer = (int)Math.Floor(polyline[0].Z / layerHeight);
                if (pathLayer > currentLayer)
                {
                    currentLayer = pathLayer;
                    gcode.WriteLine("; Layer " + currentLayer);
                    gcode.WriteLine("G0 Z" + String.Format("{0:0.###}", polyline[0].Z) + " ; 移动到新层高度");
                }

                // 移动到路径起点（不挤出）
                gcode.WriteLine("G0 X" + String.Format("{0:0.###}", polyline[0].X) +
                               " Y" + String.Format("{0:0.###}", polyline[0].Y) +
                               " ; 移动到路径起点");

                currentPosition = polyline[0];

                // 开始打印（挤出）
                gcode.WriteLine("G1 F" + printSpeed + " ; 设置打印速度");

                // 遍历路径上的所有点
                for (int i = 1; i < polyline.Count; i++)
                {
                    // 计算新的挤出量（基于移动距离）
                    double distance = currentPosition.DistanceTo(polyline[i]);
                    extrusionAmount += distance * 0.035; // 挤出系数，根据实际情况调整

                    // 生成挤出移动命令
                    gcode.WriteLine("G1 X" + String.Format("{0:0.###}", polyline[i].X) +
                                   " Y" + String.Format("{0:0.###}", polyline[i].Y) +
                                   " Z" + String.Format("{0:0.###}", polyline[i].Z) +
                                   " E" + String.Format("{0:0.###}", extrusionAmount) +
                                   " ; 打印");

                    currentPosition = polyline[i];
                }
            }

            // 添加G代码尾部
            gcode.WriteLine("G1 E" + (extrusionAmount - 1) + " F1800 ; 回抽");
            gcode.WriteLine("G0 Z" + (currentPosition.Z + 10) + " ; 抬高Z轴");
            gcode.WriteLine("G0 X0 Y0 F3600 ; 回到起点");
            gcode.WriteLine("M104 S0 ; 关闭热端");
            gcode.WriteLine("M140 S0 ; 关闭热床");
            gcode.WriteLine("M84 ; 禁用电机");

            return gcode.ToString();
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("a9e47e8d-7a15-4d6b-b018-b3d795561c86"); }
        }
    }
}