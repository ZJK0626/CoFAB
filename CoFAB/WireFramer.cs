using Grasshopper.Kernel;
using Rhino.Geometry;
using System.Linq;

namespace CoFab
{
    public class WireFramer : GH_Component
    {
        /// <summary>
        /// Constructor for the WireFramer component
        /// </summary>
        public WireFramer()
      : base("Wireframer",
             "Wireframer",
                 "Transforms a mesh into a printable wireframe structure with self-supporting properties for 3D printing",
             "CoFab",
             "Digital to Fabrication")
        {
        }

        /// <summary>
        /// Component GUID
        /// </summary>
        public override Guid ComponentGuid => new Guid("B5ECFD2F-7AE9-4FB7-94C2-A7560A021105");

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddMeshParameter("Mesh", "M", "Input mesh to transform to wireframe", GH_ParamAccess.item);
            pManager.AddNumberParameter("Contour Spacing", "CS", "Distance between contour slices", GH_ParamAccess.item, 6.0);
            pManager.AddVectorParameter("Build Direction", "D", "Build direction for 3D printing (typically Z-axis)", GH_ParamAccess.item, Vector3d.ZAxis);
            pManager.AddNumberParameter("Max Overhang Angle", "A", "Maximum allowable overhang angle in degrees", GH_ParamAccess.item, 45.0);
            pManager.AddNumberParameter("Tube Radius", "R", "Radius of the wireframe tubes", GH_ParamAccess.item, 0.5);
            pManager.AddNumberParameter("Connection Density", "CD", "Density of connections between contours (0.0-1.0)", GH_ParamAccess.item, 0.5);
            pManager.AddBooleanParameter("Optimize Structure", "Opt", "Enable structural optimization for printability", GH_ParamAccess.item, true);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
            pManager.AddMeshParameter("Wireframe", "W", "Wireframe mesh structure", GH_ParamAccess.item);
            pManager.AddCurveParameter("Contours", "C", "Extracted contour curves", GH_ParamAccess.list);
            pManager.AddCurveParameter("Pillars", "P", "Connecting pillars", GH_ParamAccess.list);
        pManager.AddTextParameter("Stats", "S", "Wireframe statistics", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
            // Input parameters
        Mesh inputMesh = null;
        double contourSpacing = 6.0;
            Vector3d buildDirection = Vector3d.ZAxis;
            double maxOverhangAngle = 45.0;
            double tubeRadius = 0.5;
            double connectionDensity = 0.5;
            bool optimizeStructure = true;

        if (!DA.GetData(0, ref inputMesh)) return;
            DA.GetData(1, ref contourSpacing);
            DA.GetData(2, ref buildDirection);
            DA.GetData(3, ref maxOverhangAngle);
            DA.GetData(4, ref tubeRadius);
            DA.GetData(5, ref connectionDensity);
            DA.GetData(6, ref optimizeStructure);

        // Validate inputs
            if (inputMesh == null || !inputMesh.IsValid)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid input mesh");
            return;
        }

            // Normalize build direction
            buildDirection.Unitize();

        // Extract contours using the scalar field
        List<Curve> contours = ExtractContours(inputMesh, scalarField, contourSpacing);

        // Create pillar connections between contours
        List<Curve> pillars = CreatePillarConnections(contours, vertexDensity, new Vector3d(0, 0, 1), supportAngle);

        // Filter pillars to target count
        int targetPillarCount = Math.Min(pillars.Count, targetEdgeCount * 2 / 3);
        pillars = FilterPillars(pillars, targetPillarCount, new Vector3d(0, 0, 1));

        // Build wireframe mesh
        Mesh wireframeMesh = new Mesh();
        if (contours.Count > 0 || pillars.Count > 0)
        {
            wireframeMesh = CreateWireframeMesh(contours, pillars, pipeRadius);
        }

        // Calculate statistics
        string stats = string.Format(
            "Wireframe Statistics:\n" +
            "Contours: {0}\n" +
            "Pillars: {1}\n" +
            "Total Edges: {2}",
            contours.Count,
            pillars.Count,
            contours.Count + pillars.Count);

        // Output
        DA.SetData(0, wireframeMesh);
        DA.SetDataList(1, contours);
        DA.SetDataList(2, pillars);
        DA.SetData(3, stats);
    }

    private Dictionary<int, double> GenerateScalarField(Mesh mesh)
    {
        Dictionary<int, double> scalarField = new Dictionary<int, double>();

        // Initialize field with Z values
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            scalarField[i] = mesh.Vertices[i].Z;
        }

        // Create Z-gradient field - keeping it simple to avoid errors
        for (int iter = 0; iter < 3; iter++)
        {
            Dictionary<int, double> newField = new Dictionary<int, double>(scalarField);

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                // Get connected vertices
                List<int> neighbors = new List<int>();
                for (int f = 0; f < mesh.Faces.Count; f++)
                {
                    MeshFace face = mesh.Faces[f];
                    if (face.A == i || face.B == i || face.C == i || (face.IsQuad && face.D == i))
                    {
                        // Add all vertices from this face
                        if (face.A != i) neighbors.Add(face.A);
                        if (face.B != i) neighbors.Add(face.B);
                        if (face.C != i) neighbors.Add(face.C);
                        if (face.IsQuad && face.D != i) neighbors.Add(face.D);
                    }
                }

                if (neighbors.Count > 0)
                {
                    // Calculate average value of neighbors
                    double avgValue = 0;
                    foreach (int n in neighbors)
                    {
                        avgValue += scalarField[n];
                    }
                    avgValue /= neighbors.Count;

                    // Mix current value with average of neighbors
                    newField[i] = 0.3 * scalarField[i] + 0.7 * avgValue;
                }
            }

            scalarField = newField;
        }

        return scalarField;
    }

    private List<Curve> ExtractContours(Mesh mesh, Dictionary<int, double> scalarField, double spacing)
    {
        List<Curve> contours = new List<Curve>();

        // 找到最小/最大字段值
        double minValue = double.MaxValue;
        double maxValue = double.MinValue;

        foreach (double value in scalarField.Values)
        {
            minValue = Math.Min(minValue, value);
            maxValue = Math.Max(maxValue, value);
        }

        // 生成轮廓值 - 显式包含最小和最大高度
        List<double> contourValues = new List<double>();

        // 添加最小Z高度作为第一个轮廓
        contourValues.Add(minValue);

        // 添加中间层轮廓
        double current = minValue + spacing;
        while (current < maxValue - (spacing * 0.5))
        {
            contourValues.Add(current);
            current += spacing;
        }

        // 添加最大Z高度作为最后一个轮廓
        contourValues.Add(maxValue);

        // 使用Rhino内置的网格截面创建轮廓线
        Plane sectionPlane;
        foreach (double zValue in contourValues)
        {
            sectionPlane = new Plane(new Point3d(0, 0, zValue), new Vector3d(0, 0, 1));

            // 这个返回Polyline[]而不是Curve[]
            Polyline[] sections = Rhino.Geometry.Intersect.Intersection.MeshPlane(mesh, sectionPlane);

            if (sections != null && sections.Length > 0)
            {
                foreach (Polyline poly in sections)
                {
                    // 确保我们处理封闭轮廓
                    if (!poly.IsClosed && poly.Count > 2)
                    {
                        // 如果聚合物接近封闭但实际上并未封闭，则关闭它
                        if (poly[0].DistanceTo(poly[poly.Count - 1]) < spacing * 0.1)
                        {
                            Polyline closedPoly = poly.Duplicate();
                            closedPoly.Add(poly[0]);
                            contours.Add(closedPoly.ToNurbsCurve());
                        }
                        else
                        {
                            contours.Add(poly.ToNurbsCurve());
                        }
                    }
                    else
                    {
                        contours.Add(poly.ToNurbsCurve());
                    }
                }
            }
        }

        return contours;
    }



    private List<Curve> CreatePillarConnections(List<Curve> contours, double minDistance, Vector3d printDirection, double maxAngle)
    {
        List<Curve> pillars = new List<Curve>();

        // 按Z水平对轮廓进行分组
        Dictionary<double, List<Curve>> contoursByZ = new Dictionary<double, List<Curve>>();

        foreach (Curve c in contours)
        {
            if (c.GetLength() < 0.01) continue; // 跳过微小轮廓

            // 使用轮廓的实际Z值而不是四舍五入 - 确保我们捕获确切的最小值和最大值
            double z = c.PointAtStart.Z;

            if (!contoursByZ.ContainsKey(z))
            {
                contoursByZ[z] = new List<Curve>();
            }

            contoursByZ[z].Add(c);
        }

        // 对Z水平进行排序
        List<double> zLevels = contoursByZ.Keys.ToList();
        zLevels.Sort();

        // 确保我们有2个或更多级别，否则无法创建支柱
        if (zLevels.Count < 2)
            return pillars;

        // 在相邻级别之间创建连接
        for (int i = 0; i < zLevels.Count - 1; i++)
        {
            double lowerZ = zLevels[i];
            double upperZ = zLevels[i + 1];

            List<Curve> lowerContours = contoursByZ[lowerZ];
            List<Curve> upperContours = contoursByZ[upperZ];

            // 以较小的间距在下部轮廓上采样点
            List<Point3d> lowerPoints = SamplePointsOnCurves(lowerContours, minDistance * 1.2);

            // 以较小的间距在上部轮廓上采样点
            List<Point3d> upperPoints = SamplePointsOnCurves(upperContours, minDistance * 1.2);

            // 创建支柱连接
            // 使用原始代码，但为顶部和底部轮廓添加更多连接
            double connectionFactor = 1.0;

            // 如果是最底层或最顶层，增加连接数
            if (i == 0 || i == zLevels.Count - 2)
            {
                connectionFactor = 0.8; // 减小最小距离要求，创建更多支柱
            }

            foreach (Point3d lowerPt in lowerPoints)
            {
                if (upperPoints.Count == 0) break;

                // 找到形成自支撑支柱的最近上点
                double minDist = double.MaxValue;
                int bestIndex = -1;

                for (int j = 0; j < upperPoints.Count; j++)
                {
                    Point3d upperPt = upperPoints[j];

                    // 检查连接是否自支撑
                    Vector3d connection = upperPt - lowerPt;
                    double angle = Vector3d.VectorAngle(connection, printDirection);

                    if (angle <= (maxAngle * Math.PI / 180.0))
                    {
                        double dist = lowerPt.DistanceTo(upperPt);
                        if (dist < minDist)
                        {
                            minDist = dist;
                            bestIndex = j;
                        }
                    }
                }

                // 如果找到合适的连接，则创建支柱
                if (bestIndex >= 0 && minDist < minDistance * 5 * connectionFactor)
                {
                    Line pillar = new Line(lowerPt, upperPoints[bestIndex]);
                    pillars.Add(pillar.ToNurbsCurve());

                    // 移除已使用的点，避免到同一点的多个支柱
                    upperPoints.RemoveAt(bestIndex);
                }
            }
        }

        return pillars;
    }

    private List<Point3d> SamplePointsOnCurves(List<Curve> curves, double spacing)
    {
        List<Point3d> points = new List<Point3d>();

        foreach (Curve curve in curves)
        {
            double length = curve.GetLength();

            // 减小除数，增加采样点数量
            int divisions = Math.Max(4, (int)(length / (spacing * 1.5)));

            Point3d[] curvePoints = null;
            curve.DivideByCount(divisions, true, out curvePoints);

            if (curvePoints != null && curvePoints.Length > 0)
            {
                // 改为添加更多点 - 只跳过部分点而不是每隔一个取样
                for (int i = 0; i < curvePoints.Length; i += 1)  // 原来是i += 2
                {
                    points.Add(curvePoints[i]);
                }
            }
        }

        return points;
    }

    private List<Curve> FilterPillars(List<Curve> pillars, int targetCount, Vector3d printDirection)
    {
        if (pillars.Count <= targetCount)
            return pillars;

        // Score each pillar based on length and alignment with print direction
        List<KeyValuePair<double, Curve>> scoredPillars = new List<KeyValuePair<double, Curve>>();

        foreach (Curve pillar in pillars)
        {
            Point3d start = pillar.PointAtStart;
            Point3d end = pillar.PointAtEnd;
            Vector3d direction = end - start;
            double length = direction.Length;

            if (length < 0.001) continue; // Skip zero-length pillars

            direction.Unitize();

            // Score based on alignment with print direction (higher is better)
            double alignmentScore = Math.Abs(Vector3d.Multiply(direction, printDirection));

            // Score based on length (shorter is better)
            double lengthScore = 1.0 / (length + 0.1);

            // Combined score
            double score = alignmentScore * 0.7 + lengthScore * 0.3;

            scoredPillars.Add(new KeyValuePair<double, Curve>(score, pillar));
        }

        // Sort by score (higher first)
        scoredPillars.Sort((a, b) => b.Key.CompareTo(a.Key));

        // Take top scorers
        return scoredPillars.Take(targetCount).Select(pair => pair.Value).ToList();
    }

    private Mesh CreateWireframeMesh(List<Curve> contours, List<Curve> pillars, double radius)
    {
        Mesh result = new Mesh();

        // Combine all edges
        List<Curve> allCurves = new List<Curve>();
        allCurves.AddRange(contours);
        allCurves.AddRange(pillars);

        // Create pipe meshes for each curve
        foreach (Curve curve in allCurves)
        {
            Mesh pipeMesh = CreatePipeMesh(curve, radius);
            if (pipeMesh != null)
            {
                result.Append(pipeMesh);
            }
        }

        if (result.Vertices.Count > 0)
        {
            result.Weld(Math.PI / 4); // Weld vertices to clean up the mesh
            result.RebuildNormals();
            result.Compact();
        }

        return result;
    }

    private Mesh CreatePipeMesh(Curve curve, double radius)
    {
        if (curve == null || curve.GetLength() < 0.01)
            return null;

            // Format stats string
            string stats = string.Format(
                "Wireframe Statistics:\n" +
                "Contours: {0}\n" +
                "Pillars: {1}\n" +
                "Total Length: {2:F2} units\n" +
                "Self-supporting: {3:F1}%",
                contours.Count,
                pillars.Count,
                totalLength,
                supportedPercentage);

        if (pipes == null || pipes.Length == 0)
            return null;

        // Convert to mesh with lower resolution
        Mesh pipeMesh = new Mesh();
        foreach (Brep pipe in pipes)
        {
            MeshingParameters mp = new MeshingParameters();
            mp.SimplePlanes = true;
            mp.RefineGrid = false;
            // Reduce mesh complexity
            mp.GridMaxCount = 16;  // Lower value for fewer polygons
            mp.GridAmplification = 1.0;
            mp.GridAngle = 30;  // Higher value for fewer polygons
            mp.GridAspectRatio = 2.0;  // Higher for fewer polygons
            mp.JaggedSeams = false;
            mp.MaximumEdgeLength = radius * 8;  // Higher for fewer edges

        public PointEqualityComparer(double tolerance)
                {
            _tolerance = tolerance;
            }
        }

        public int GetHashCode(Point3d obj)
        {
            // Round to reduce hash collisions while maintaining tolerance
            double scale = 1.0 / _tolerance;
            return (Math.Round(obj.X * scale) +
                   Math.Round(obj.Y * scale) * 1000 +
                   Math.Round(obj.Z * scale) * 1000000).GetHashCode();
        }
    }
}