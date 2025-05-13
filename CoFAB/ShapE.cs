using System;
using System.Diagnostics; 
using System.IO;  
using Grasshopper;
using Grasshopper.Kernel;
using Rhino.Geometry;  
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class ShapEComponent : GH_Component
{
    public ShapEComponent()
      : base("ShapE Generator",
             "ShapE",
             "Generates a 3D model via Shap-E Python script",
             "CoFab", 
             "AI-assisted 3D Generator")
    { }

    public override Guid ComponentGuid => new Guid("A0CC66F7-3D67-4559-94AD-E06B6F0D92E1"); 

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddTextParameter("Prompt", "Prompt", "Text prompt for Shap-E", GH_ParamAccess.item, "a coffee mug");
        pManager.AddNumberParameter("GuidanceScale", "G", "Guidance scale factor", GH_ParamAccess.item, 12.0);
        pManager.AddIntegerParameter("KarrasSteps", "Steps", "Number of sampling steps (Calculation_steps)", GH_ParamAccess.item, 64);
        pManager.AddBooleanParameter("Run", "Run", "Set to true to run generation", GH_ParamAccess.item, false);
        pManager.AddBooleanParameter("Remesh", "Remesh", "If true, run QuadReMesh after generation", GH_ParamAccess.item, true);
        pManager.AddIntegerParameter("TargetQuads", "TQ", "Approximate target quad count", GH_ParamAccess.item, 2000);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Generated mesh from Shap-E", GH_ParamAccess.item);
        pManager.AddTextParameter("ConsoleLog", "Log", "Console output from the Python script", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string prompt = "";
        double guidanceScale = 12.0;
        int steps = 64;
        bool runGeneration = false;
        bool doRemesh = true;
        int targetQuadCount = 2000;

        if (!DA.GetData(0, ref prompt)) return;
        if (!DA.GetData(1, ref guidanceScale)) return;
        if (!DA.GetData(2, ref steps)) return;
        if (!DA.GetData(3, ref runGeneration)) return;
        if (!DA.GetData(4, ref doRemesh)) return;
        if (!DA.GetData(5, ref targetQuadCount)) return;

        if (!runGeneration) return;

        string pythonExe = @"C:\Users\DELL\anaconda3\envs\shap_e_env\python.exe";
        string scriptPath = @"C:\Users\DELL\shap-e\shap_e_generate.py";
        string tempDir = Path.GetTempPath();
        string objFile = Path.Combine(tempDir, $"shap_e_{Guid.NewGuid()}.obj");


        ProcessStartInfo psi = new ProcessStartInfo()
        {
            FileName = pythonExe,
            Arguments = $"\"{scriptPath}\" \"{prompt}\" {guidanceScale} {steps} \"{objFile}\"",
            UseShellExecute = true,
            CreateNoWindow = false,
        };

        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(scriptPath);

        Process p = new Process();
        p.StartInfo = psi;

        try
        {
            p.Start();
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

        Mesh ghMesh = new Mesh();
        if (File.Exists(objFile))
        {
            ghMesh = LoadObjAsMesh(objFile);
        }
        else
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "OBJ file not found: " + objFile);
        }

        if (doRemesh)
        {
            var quadParams = new Rhino.Geometry.QuadRemeshParameters();
            quadParams.TargetQuadCount = targetQuadCount;

            Mesh remeshed = ghMesh.QuadRemesh(quadParams);
            if (remeshed != null)
            {
                ghMesh = remeshed;
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "QuadRemesh failed to generate a new mesh.");
            }
        }

        ScaleMeshToMaxDimension(ref ghMesh, 100.0);

        DA.SetData(0, ghMesh);
    }

    private void ScaleMeshToMaxDimension(ref Mesh mesh, double targetSize)
    {
        var bbox = mesh.GetBoundingBox(false);
        double sizeX = bbox.Max.X - bbox.Min.X;
        double sizeY = bbox.Max.Y - bbox.Min.Y;
        double sizeZ = bbox.Max.Z - bbox.Min.Z;
        double maxDim = Math.Max(sizeX, Math.Max(sizeY, sizeZ));

        if (maxDim < 1e-6)
            return; 

        double scaleFactor = targetSize / maxDim;

        var center = bbox.Center;
        var xform = Rhino.Geometry.Transform.Scale(center, scaleFactor);


        mesh.Transform(xform);
    }

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

        foreach (var v in verts)
            m.Vertices.Add(v);

        foreach (var line in lines)
        {
            if (line.StartsWith("f "))
            {
                var parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    try
                    {
                        int v1 = int.Parse(parts[1].Split('/')[0]) - 1;
                        int v2 = int.Parse(parts[2].Split('/')[0]) - 1;
                        int v3 = int.Parse(parts[3].Split('/')[0]) - 1;
                        m.Faces.AddFace(v1, v2, v3);
                    }
                    catch (Exception)
                    {

                    }
                }
            }
        }
        m.Normals.ComputeNormals();
        return m;
    }
}
