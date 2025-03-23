using System;
using System.Diagnostics;  // for Process
using System.IO;          // for file IO
using Grasshopper;
using Grasshopper.Kernel;
using Rhino.Geometry;     // for Mesh geometry
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class ShapEComponent : GH_Component
{
    public ShapEComponent()
      : base("ShapE Generator",
             "ShapEGen",
             "Generates a 3D model via Shap-E Python script",
             "GPT2GH", // 你想放在哪个Grasshopper tab下面
             "Text2Mesh")
    { }

    public override Guid ComponentGuid => new Guid("43C09819-C826-486E-994F-EDB6490A0B33"); 

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddTextParameter("Prompt", "Prompt", "Text prompt for Shap-E", GH_ParamAccess.item, "a coffee mug");
        pManager.AddNumberParameter("GuidanceScale", "G", "Guidance scale factor", GH_ParamAccess.item, 15.0);
        pManager.AddIntegerParameter("KarrasSteps", "Steps", "Number of sampling steps (karras_steps)", GH_ParamAccess.item, 48);
        pManager.AddBooleanParameter("Run", "Run", "Set to true to run generation", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Generated mesh from Shap-E", GH_ParamAccess.item);
        // 如果需要输出更多信息，可加参数
        pManager.AddTextParameter("ConsoleLog", "Log", "Console output from the Python script", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        // 1. 获取输入
        string prompt = "";
        double guidanceScale = 12.0;
        int steps = 64;
        bool runGeneration = false;
        if (!DA.GetData(0, ref prompt)) return;
        if (!DA.GetData(1, ref guidanceScale)) return;
        if (!DA.GetData(2, ref steps)) return;
        if (!DA.GetData(3, ref runGeneration)) return;

        // 如果没勾选 Run，就不执行
        if (!runGeneration) return;

        // 2. 调用外部 Python 脚本
        string pythonExe = @"C:\Users\93914\anaconda3\envs\shap_e_env\python.exe";
        string scriptPath = @"C:\Users\93914\shap-e\shap_e_generate.py";
        string tempDir = Path.GetTempPath();
        string objFile = Path.Combine(tempDir, $"shap_e_{Guid.NewGuid()}.obj");

        // 使用 ShellExecute = true，将在单独的终端窗口里运行脚本
        // 无法再重定向标准输出/错误流
        ProcessStartInfo psi = new ProcessStartInfo()
        {
            FileName = pythonExe,
            Arguments = $"\"{scriptPath}\" \"{prompt}\" {guidanceScale} {steps} \"{objFile}\"",
            UseShellExecute = true,         // 必须为 true 才能弹出独立窗口
            CreateNoWindow = false,         // false 表示让 Python 窗口可见
        };

        // 可选：指定工作目录，避免相对路径问题
        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(scriptPath);

        Process p = new Process();
        p.StartInfo = psi;

        try
        {
            p.Start();
            // 等待脚本执行完毕，阻塞当前线程
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Python script failed with exit code {p.ExitCode}");
                return;
            }
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Exception running python: {ex.Message}");
            return;
        }

        // 3. 解析 OBJ → Rhino Mesh
        Mesh ghMesh = new Mesh();
        if (File.Exists(objFile))
        {
            ghMesh = LoadObjAsMesh(objFile);
        }
        else
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "OBJ file not found: " + objFile);
        }

        // 对网格做自动缩放，让最大维度 = 100
        ScaleMeshToMaxDimension(ref ghMesh, 100.0);

        // 4. 输出：网格
        DA.SetData(0, ghMesh);
    }

    private void ScaleMeshToMaxDimension(ref Mesh mesh, double targetSize)
    {
        // 获取网格的包围框
        var bbox = mesh.GetBoundingBox(false);
        // 计算 X/Y/Z 三个方向的长度
        double sizeX = bbox.Max.X - bbox.Min.X;
        double sizeY = bbox.Max.Y - bbox.Min.Y;
        double sizeZ = bbox.Max.Z - bbox.Min.Z;
        // 找到最大的一个维度
        double maxDim = Math.Max(sizeX, Math.Max(sizeY, sizeZ));

        // 若 maxDim 太小，避免除零
        if (maxDim < 1e-6)
            return; // 网格可能是个空对象或极小，不缩放

        // 计算缩放比例
        double scaleFactor = targetSize / maxDim;
        // 以包围框中心点为基准缩放（也可以用 bbox.Min 或者世界原点等）
        var center = bbox.Center;
        var xform = Rhino.Geometry.Transform.Scale(center, scaleFactor);

        // 对网格应用变换
        mesh.Transform(xform);
    }

    // 一个非常简陋的 OBJ 解析示例，只解析 v/f 三角面
    // 如果 OBJ 面是四边形/其它复杂格式，需要做更多逻辑
    private Mesh LoadObjAsMesh(string path)
    {
        Mesh m = new Mesh();
        var lines = File.ReadAllLines(path);
        List<Point3d> verts = new List<Point3d>();
        foreach (var line in lines)
        {
            if (line.StartsWith("v "))
            {
                var parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    double x = double.Parse(parts[1]);
                    double y = double.Parse(parts[2]);
                    double z = double.Parse(parts[3]);
                    verts.Add(new Point3d(x, y, z));
                }
            }
        }
        // 先把所有顶点加到网格
        foreach (var v in verts)
            m.Vertices.Add(v);

        foreach (var line in lines)
        {
            if (line.StartsWith("f "))
            {
                // 解析面信息
                // e.g. "f 1 2 3" or "f 1/tex 2/tex 3/tex"
                var parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    // 把每个 parts[i] 仅取"/"前面那段
                    try
                    {
                        int v1 = int.Parse(parts[1].Split('/')[0]) - 1; // OBJ 索引从1开始
                        int v2 = int.Parse(parts[2].Split('/')[0]) - 1;
                        int v3 = int.Parse(parts[3].Split('/')[0]) - 1;
                        m.Faces.AddFace(v1, v2, v3);
                    }
                    catch (Exception)
                    {
                        // 解析失败就忽略
                    }
                }
            }
        }
        m.Normals.ComputeNormals();
        return m;
    }
}
