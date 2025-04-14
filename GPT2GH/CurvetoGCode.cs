using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace CoFab
{
    public class CurveToGCodeComponent : GH_Component
    {
        /// <summary>
        /// Constructor for the curve-to-GCode component
        /// </summary>
        public CurveToGCodeComponent()
          : base("GCode Generation",
                 "CurveGCode",
                 "Convert curves and lines to G-code for robotic arm 3D printers and CNC machines",
                 "CoFab",
                 "Digital to Fabrication")
        {
        }

        /// <summary>
        /// Register all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // Primary geometry input - can be curves or lines
            pManager.AddCurveParameter("Curves", "C", "Curves to convert to G-code", GH_ParamAccess.list);

            // Machine settings
            pManager.AddNumberParameter("Print Speed", "PS", "Print speed in mm/min", GH_ParamAccess.item, 1200.0);
            pManager.AddNumberParameter("Travel Speed", "TS", "Travel (rapid) speed in mm/min", GH_ParamAccess.item, 3000.0);
            pManager.AddNumberParameter("Z Height", "Z", "Working Z height for printing", GH_ParamAccess.item, 0.2);
            pManager.AddNumberParameter("Safe Z", "SZ", "Safe Z height for travel moves", GH_ParamAccess.item, 10.0);

            // Hotend temperature
            pManager.AddNumberParameter("Print Temp", "PT", "Print temperature in °C", GH_ParamAccess.item, 205.0);

            // 3D printing specific options
            pManager.AddNumberParameter("Layer Height", "LH", "Layer height for 3D printing (mm)", GH_ParamAccess.item, 0.2);
            pManager.AddNumberParameter("Extrusion Width", "EW", "Extrusion width for 3D printing (mm)", GH_ParamAccess.item, 0.4);
            pManager.AddNumberParameter("Extrusion Multiplier", "EM", "Extrusion multiplier/flow rate", GH_ParamAccess.item, 1.0);
            pManager.AddBooleanParameter("Use M2000", "M2000", "Enable M2000 line-mode command (robotic arm specific)", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Use M888", "M888", "Enable M888 P3 current mode command (robotic arm specific)", GH_ParamAccess.item, true);

            // Add diagonal support parameters
            pManager.AddBooleanParameter("Add Supports", "AS", "Add diagonal supports between layers", GH_ParamAccess.item, true);
            pManager.AddNumberParameter("Support Density", "SD", "Support structure density (1-10)", GH_ParamAccess.item, 5.0);

            // Output file path
            pManager.AddTextParameter("Output File", "Out", "Path to save the G-code file", GH_ParamAccess.item, "output.gcode");

            // Optional parameters
            pManager.AddBooleanParameter("Sort Curves", "Sort", "Sort curves to minimize travel moves", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Export", "E", "Set to true to write the G-code file", GH_ParamAccess.item, false);
        }

        /// <summary>
        /// Register all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("G-code", "G", "Generated G-code text", GH_ParamAccess.item);
            pManager.AddCurveParameter("Tool Path", "TP", "The ordered tool path for preview", GH_ParamAccess.list);
            pManager.AddTextParameter("Status", "S", "Operation status", GH_ParamAccess.item);
        }

        /// <summary>
        /// Solve instance: Process data, generate G-code, and handle exports
        /// </summary>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Input: Curves and lines to convert
            List<Curve> curves = new List<Curve>();
            if (!DA.GetDataList(0, curves) || curves.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No curves provided");
                return;
            }

            // Input: Machine settings
            double printSpeed = 1200.0;
            double travelSpeed = 3000.0;
            double zHeight = 0.2;
            double safeZ = 10.0;
            DA.GetData(1, ref printSpeed);
            DA.GetData(2, ref travelSpeed);
            DA.GetData(3, ref zHeight);
            DA.GetData(4, ref safeZ);

            // Hotend temperature
            double printTemp = 205.0;
            DA.GetData(5, ref printTemp);

            // Input: 3D printing settings
            double layerHeight = 0.2;
            double extrusionWidth = 0.4;
            double extrusionMultiplier = 1.0;
            bool useM2000 = true;
            bool useM888 = true;
            DA.GetData(6, ref layerHeight);
            DA.GetData(7, ref extrusionWidth);
            DA.GetData(8, ref extrusionMultiplier);
            DA.GetData(9, ref useM2000);
            DA.GetData(10, ref useM888);

            // Support settings
            bool addSupports = true;
            double supportDensity = 5.0;
            DA.GetData(11, ref addSupports);
            DA.GetData(12, ref supportDensity);

            // Input: File output settings
            string outputPath = "output.gcode";
            bool sortCurves = true;
            bool export = false;
            DA.GetData(13, ref outputPath);
            DA.GetData(14, ref sortCurves);
            DA.GetData(15, ref export);

            // Sort curves to minimize travel distance if requested
            List<Curve> orderedCurves = curves;
            if (sortCurves && curves.Count > 1)
            {
                orderedCurves = SortCurvesForMinimumTravel(curves);
            }

            // Discretize curves into polylines (for easier processing)
            List<Polyline> paths = new List<Polyline>();
            foreach (Curve curve in orderedCurves)
            {
                // Try to get polyline directly
                Polyline poly = new Polyline();
                bool polylineExtracted = false;

                if (curve is PolylineCurve)
                {
                    PolylineCurve polyCurve = curve as PolylineCurve;
                    poly = new Polyline();
                    for (int i = 0; i < polyCurve.PointCount; i++)
                    {
                        poly.Add(polyCurve.Point(i));
                    }
                    paths.Add(poly);
                    polylineExtracted = true;
                }
                else
                {
                    Polyline tempPoly;
                    if (curve.TryGetPolyline(out tempPoly))
                    {
                        paths.Add(tempPoly);
                        polylineExtracted = true;
                    }
                }

                if (!polylineExtracted)
                {
                    // Discretize the curve with appropriate tolerance
                    double tolerance = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;

                    // Create a polyline with the correct number of points
                    int pointCount = Math.Max(10, (int)(curve.GetLength() / tolerance));
                    poly = new Polyline(pointCount);

                    // Add the start point
                    poly.Add(curve.PointAtStart);

                    // Add intermediate points
                    for (int i = 1; i < pointCount - 1; i++)
                    {
                        double t = (double)i / (pointCount - 1);
                        poly.Add(curve.PointAt(t));
                    }

                    // Add the end point
                    poly.Add(curve.PointAtEnd);

                    paths.Add(poly);
                }
            }

            // Calculate approximate model dimensions
            BoundingBox bounds = CalculateBoundingBox(paths);

            // Generate G-code for the robotic arm printer
            string gcode = GenerateRoboticArmGCode(
                paths, printSpeed, travelSpeed, zHeight, safeZ,
                layerHeight, extrusionWidth, extrusionMultiplier,
                printTemp, useM2000, useM888, bounds, addSupports, supportDensity
            );

            // Write the G-code file if export is enabled
            string status = "G-code generated, but not exported";
            if (export)
            {
                try
                {
                    File.WriteAllText(outputPath, gcode);
                    status = $"G-code exported to: {outputPath}";
                }
                catch (Exception ex)
                {
                    status = $"Failed to export G-code: {ex.Message}";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, status);
                }
            }

            // Output: G-code text, tool path for preview, and status
            DA.SetData(0, gcode);
            DA.SetDataList(1, orderedCurves);
            DA.SetData(2, status);
        }

        /// <summary>
        /// Calculate bounding box for all paths
        /// </summary>
        private BoundingBox CalculateBoundingBox(List<Polyline> paths)
        {
            BoundingBox bounds = BoundingBox.Empty;

            foreach (Polyline path in paths)
            {
                foreach (Point3d pt in path)
                {
                    bounds.Union(pt);
                }
            }

            return bounds;
        }

        /// <summary>
        /// Generate G-code for robotic arm 3D printer
        /// </summary>
        private string GenerateRoboticArmGCode(
            List<Polyline> paths, double printSpeed, double travelSpeed,
            double zHeight, double safeZ, double layerHeight,
            double extrusionWidth, double extrusionMultiplier,
            double printTemp, bool useM2000, bool useM888, BoundingBox bounds,
            bool addSupports, double supportDensity)
        {
            StringBuilder sb = new StringBuilder();

            // Estimated print time - you can implement a more accurate calculation if needed
            int estimatedPrintTimeSeconds = EstimatePrintTime(paths, printSpeed, travelSpeed);

            // Add G-code header based on the robot arm printer format
            sb.AppendLine(";FLAVOR:Marlin");
            sb.AppendLine($";TIME:{estimatedPrintTimeSeconds}");
            sb.AppendLine(";Filament used: 0m");
            sb.AppendLine($";Layer height: {layerHeight}");
            sb.AppendLine($";MINX:{bounds.Min.X:F3}");
            sb.AppendLine($";MINY:{bounds.Min.Y:F3}");
            sb.AppendLine($";MINZ:{bounds.Min.Z:F3}");
            sb.AppendLine($";MAXX:{bounds.Max.X:F3}");
            sb.AppendLine($";MAXY:{bounds.Max.Y:F3}");
            sb.AppendLine($";MAXZ:{bounds.Max.Z:F3}");
            sb.AppendLine();
            sb.AppendLine(";Generated with CoFab CurveToGCode");
            sb.AppendLine($"M104 S{printTemp}");
            sb.AppendLine("M105");
            sb.AppendLine($"M109 S{printTemp}");
            sb.AppendLine("M82 ;absolute extrusion mode");

            // Start G-code section
            sb.AppendLine(";---------- start gcode ----------");
            if (useM2000)
                sb.AppendLine("M2000 ;custom: set to line-mode");
            if (useM888)
                sb.AppendLine("M888 P3 ;custom: current is p3d");
            sb.AppendLine($"G1 Z{safeZ} F1000");
            sb.AppendLine("G92 E0");
            sb.AppendLine("G1 F200 E3");
            sb.AppendLine("G92 E0");
            sb.AppendLine(";---------------------------------");
            sb.AppendLine("G92 E0");
            sb.AppendLine("G92 E0");
            sb.AppendLine("G1 F2400 E-6");

            // Sort paths by Z height to handle 3D structures (important for proper printing)
            var pathsByZ = OrganizePathsByZ(paths, layerHeight);

            // Get the layer heights in ascending order
            List<double> layerHeights = new List<double>(pathsByZ.Keys);
            layerHeights.Sort();

            sb.AppendLine($";LAYER_COUNT:{layerHeights.Count}");

            // Setup variables for extrusion calculation
            double extrusionAmount = 0;
            Point3d currentPosition = new Point3d(0, 0, 0);
            double volumePerMM = extrusionWidth * layerHeight * extrusionMultiplier;
            // Assuming 1.75mm filament
            double filamentDiameter = 1.75;
            double filamentArea = Math.PI * (filamentDiameter / 2) * (filamentDiameter / 2);
            double extrusionFactor = volumePerMM / filamentArea;

            // Process each layer of paths
            int layerIndex = 0;
            Point3d lastLayerPoint = new Point3d(0, 0, 0); // Track the last point in the previous layer

            foreach (double layerZ in layerHeights)
            {
                List<Polyline> layerPaths = pathsByZ[layerZ];
                sb.AppendLine($";LAYER:{layerIndex}");

                if (layerIndex > 0 && addSupports)
                {
                    // Add support structures between layers
                    sb.AppendLine(";TYPE:SUPPORT");

                    // Calculate the number of support structures based on density
                    int numSupports = (int)Math.Ceiling(supportDensity * 2);

                    // Create support paths
                    List<Polyline> supportPaths = GenerateSupportPaths(
                        pathsByZ[layerHeights[layerIndex - 1]],
                        layerPaths,
                        layerHeights[layerIndex - 1],
                        layerZ,
                        numSupports);

                    // Add the support path G-code
                    foreach (Polyline supportPath in supportPaths)
                    {
                        // Only add support if it has at least 2 points
                        if (supportPath.Count < 2) continue;

                        // Move to the start position of the support
                        sb.AppendLine($"G0 F{travelSpeed} X{supportPath[0].X:F3} Y{supportPath[0].Y:F3} Z{supportPath[0].Z:F3}");

                        // Extrude along the support path
                        for (int i = 1; i < supportPath.Count; i++)
                        {
                            Point3d pt = supportPath[i];

                            // Calculate extrusion amount
                            double distance = currentPosition.DistanceTo(pt);
                            extrusionAmount += distance * extrusionFactor;

                            // First point with speed
                            if (i == 1)
                            {
                                sb.AppendLine($"G1 F{printSpeed} X{pt.X:F3} Y{pt.Y:F3} Z{pt.Z:F3} E{extrusionAmount:F5}");
                            }
                            else
                            {
                                sb.AppendLine($"G1 X{pt.X:F3} Y{pt.Y:F3} Z{pt.Z:F3} E{extrusionAmount:F5}");
                            }

                            currentPosition = pt;
                        }
                    }
                }

                // Process each path in this layer
                sb.AppendLine("M107");
                foreach (Polyline path in layerPaths)
                {
                    if (path.Count < 2) continue;

                    // Move to safe Z height before traveling to new path start
                    sb.AppendLine($"G0 F{travelSpeed} X{path[0].X:F3} Y{path[0].Y:F3} Z{safeZ:F3}");

                    // Type tag for wall (matches the sample format)
                    sb.AppendLine(";TYPE:WALL-INNER");

                    // Reset extrusion if we're still at the beginning
                    if (layerIndex == 0 && currentPosition.X == 0 && currentPosition.Y == 0 && currentPosition.Z == 0)
                    {
                        sb.AppendLine("G1 F2400 E0");
                    }

                    // Move down to the actual Z height of this path
                    sb.AppendLine($"G0 Z{layerZ:F3}");

                    // Process all points in the path
                    currentPosition = path[0];
                    for (int i = 1; i < path.Count; i++)
                    {
                        Point3d pt = path[i];

                        // Calculate extrusion amount
                        double distance = currentPosition.DistanceTo(pt);
                        extrusionAmount += distance * extrusionFactor;

                        // Format matches the sample (F speed only on first segment of path)
                        if (i == 1)
                        {
                            sb.AppendLine($"G1 F{printSpeed} X{pt.X:F3} Y{pt.Y:F3} E{extrusionAmount:F5}");
                        }
                        else
                        {
                            // For a more flat structure, don't include Z coordinate in regular path
                            sb.AppendLine($"G1 X{pt.X:F3} Y{pt.Y:F3} E{extrusionAmount:F5}");
                        }
                        currentPosition = pt;
                    }

                    // Store the last point of this layer for support generation
                    lastLayerPoint = currentPosition;
                }

                layerIndex++;
            }

            // End G-code
            sb.AppendLine("G1 F2400 E0");
            sb.AppendLine("M107");
            sb.AppendLine(";----------- end gcode -----------");
            sb.AppendLine(";move header up 10mm");
            sb.AppendLine("G91");
            sb.AppendLine("G0 Z10");
            sb.AppendLine("G90");
            sb.AppendLine("M104 S0");
            sb.AppendLine(";retract the filament");
            sb.AppendLine("G92 E1");
            sb.AppendLine("G1 E-1 F300");
            sb.AppendLine(";---------------------------------");
            sb.AppendLine("M82 ;absolute extrusion mode");
            sb.AppendLine("M104 S0");
            sb.AppendLine(";End of Gcode");

            return sb.ToString();
        }

        /// <summary>
        /// Generate support paths between two layers
        /// </summary>
        private List<Polyline> GenerateSupportPaths(
            List<Polyline> lowerLayerPaths,
            List<Polyline> upperLayerPaths,
            double lowerZ,
            double upperZ,
            int numSupports)
        {
            List<Polyline> supportPaths = new List<Polyline>();

            // Get representative points from each layer
            List<Point3d> lowerPoints = SamplePointsFromPaths(lowerLayerPaths, numSupports);
            List<Point3d> upperPoints = SamplePointsFromPaths(upperLayerPaths, numSupports);

            // Match points between layers (diagonal connections)
            for (int i = 0; i < lowerPoints.Count && i < upperPoints.Count; i++)
            {
                Polyline supportPath = new Polyline();
                supportPath.Add(lowerPoints[i]);
                supportPath.Add(upperPoints[i]);
                supportPaths.Add(supportPath);
            }

            // Add a few diagonal cross-supports for structural integrity
            int crossSupports = Math.Min(lowerPoints.Count, upperPoints.Count) / 3;
            for (int i = 0; i < crossSupports; i++)
            {
                int upperIndex = (i + crossSupports) % upperPoints.Count;
                Polyline supportPath = new Polyline();
                supportPath.Add(lowerPoints[i]);
                supportPath.Add(upperPoints[upperIndex]);
                supportPaths.Add(supportPath);
            }

            // Add circumferential supports (connect points in the same layer)
            if (lowerPoints.Count >= 2)
            {
                Polyline lowerSupport = new Polyline();
                for (int i = 0; i < lowerPoints.Count; i++)
                {
                    lowerSupport.Add(lowerPoints[i]);
                }
                // Close the loop
                if (lowerPoints.Count > 0)
                {
                    lowerSupport.Add(lowerPoints[0]);
                }
                supportPaths.Add(lowerSupport);
            }

            return supportPaths;
        }

        /// <summary>
        /// Sample points from polyline paths
        /// </summary>
        private List<Point3d> SamplePointsFromPaths(List<Polyline> paths, int numPoints)
        {
            List<Point3d> sampledPoints = new List<Point3d>();

            if (paths.Count == 0 || numPoints <= 0)
                return sampledPoints;

            // Estimate the total length of all paths
            double totalLength = 0;
            foreach (Polyline path in paths)
            {
                for (int i = 1; i < path.Count; i++)
                {
                    totalLength += path[i - 1].DistanceTo(path[i]);
                }
            }

            // Calculate sampling interval
            double interval = totalLength / numPoints;
            if (interval <= 0)
                interval = 1;

            // Sample points along the paths
            double currentLength = 0;
            double nextSampleAt = interval / 2; // Start at half interval for better distribution

            foreach (Polyline path in paths)
            {
                for (int i = 1; i < path.Count; i++)
                {
                    double segmentLength = path[i - 1].DistanceTo(path[i]);

                    // Check if we need to sample within this segment
                    while (nextSampleAt <= currentLength + segmentLength && sampledPoints.Count < numPoints)
                    {
                        // Calculate parametric position along segment
                        double t = (nextSampleAt - currentLength) / segmentLength;

                        // Interpolate to get sample point
                        Point3d samplePoint = new Point3d(
                            path[i - 1].X + t * (path[i].X - path[i - 1].X),
                            path[i - 1].Y + t * (path[i].Y - path[i - 1].Y),
                            path[i - 1].Z + t * (path[i].Z - path[i - 1].Z)
                        );

                        // Add the sample point to our collection
                        sampledPoints.Add(samplePoint);

                        // Move to the next sampling position
                        nextSampleAt += interval;
                    }

                    // Update current length
                    currentLength += segmentLength;
                }
            }

            return sampledPoints;
        }

        /// <summary>
        /// Organize paths by Z height into layers
        /// </summary>
        private Dictionary<double, List<Polyline>> OrganizePathsByZ(List<Polyline> paths, double layerHeight)
        {
            Dictionary<double, List<Polyline>> pathsByZ = new Dictionary<double, List<Polyline>>();

            // First pass: determine all the unique Z heights
            HashSet<double> uniqueLayerZs = new HashSet<double>();

            foreach (Polyline path in paths)
            {
                if (path.Count < 2) continue;

                // Calculate the average Z height of the path
                double sumZ = 0;
                foreach (Point3d pt in path)
                {
                    sumZ += pt.Z;
                }
                double avgZ = sumZ / path.Count;

                // Snap to the nearest layer height
                double layerZ = Math.Round(avgZ / layerHeight) * layerHeight;
                layerZ = Math.Max(layerZ, layerHeight); // Ensure Z is at least one layer height

                uniqueLayerZs.Add(layerZ);
            }

            // Sort layer heights
            List<double> sortedZs = new List<double>(uniqueLayerZs);
            sortedZs.Sort();

            // Initialize empty lists for each layer
            foreach (double z in sortedZs)
            {
                pathsByZ[z] = new List<Polyline>();
            }

            // Second pass: assign paths to their layers
            foreach (Polyline path in paths)
            {
                if (path.Count < 2) continue;

                // Calculate the average Z height of the path
                double sumZ = 0;
                foreach (Point3d pt in path)
                {
                    sumZ += pt.Z;
                }
                double avgZ = sumZ / path.Count;

                // Snap to the nearest layer height
                double layerZ = Math.Round(avgZ / layerHeight) * layerHeight;
                layerZ = Math.Max(layerZ, layerHeight); // Ensure Z is at least one layer height

                // Create a new polyline for this layer with adjusted Z height
                Polyline adjustedPath = new Polyline();
                foreach (Point3d pt in path)
                {
                    // Keep X and Y coordinates, but adjust Z to layer height
                    adjustedPath.Add(new Point3d(pt.X, pt.Y, layerZ));
                }

                // Add adjusted path to the correct layer
                pathsByZ[layerZ].Add(adjustedPath);
            }

            return pathsByZ;
        }

        /// <summary>
        /// Estimate print time in seconds based on path lengths and speeds
        /// </summary>
        private int EstimatePrintTime(List<Polyline> paths, double printSpeed, double travelSpeed)
        {
            double totalPrintDistance = 0;
            double totalTravelDistance = 0;
            Point3d lastPoint = new Point3d(0, 0, 0);

            foreach (Polyline path in paths)
            {
                if (path.Count < 2) continue;

                // Travel to first point of path
                totalTravelDistance += lastPoint.DistanceTo(path[0]);

                // Print along the path
                for (int i = 1; i < path.Count; i++)
                {
                    totalPrintDistance += path[i - 1].DistanceTo(path[i]);
                }

                lastPoint = path[path.Count - 1];
            }

            // Calculate time in seconds
            double printTimeMinutes = totalPrintDistance / printSpeed;
            double travelTimeMinutes = totalTravelDistance / travelSpeed;

            // Add some buffer for acceleration/deceleration and other operations
            double totalTimeMinutes = (printTimeMinutes + travelTimeMinutes) * 1.2;

            return (int)(totalTimeMinutes * 60);
        }

        /// <summary>
        /// Sort curves to minimize travel distance between them
        /// </summary>
        private List<Curve> SortCurvesForMinimumTravel(List<Curve> curves)
        {
            List<Curve> result = new List<Curve>();
            List<Curve> remaining = new List<Curve>(curves);

            // Start with the curve closest to origin
            Point3d currentPoint = new Point3d(0, 0, 0);

            while (remaining.Count > 0)
            {
                double minDistance = double.MaxValue;
                int bestIndex = -1;
                bool connectToStart = true;

                // Find the closest curve (either start or end point)
                for (int i = 0; i < remaining.Count; i++)
                {
                    Curve curve = remaining[i];

                    double distToStart = currentPoint.DistanceTo(curve.PointAtStart);
                    double distToEnd = currentPoint.DistanceTo(curve.PointAtEnd);

                    if (distToStart < minDistance)
                    {
                        minDistance = distToStart;
                        bestIndex = i;
                        connectToStart = true;
                    }

                    if (distToEnd < minDistance)
                    {
                        minDistance = distToEnd;
                        bestIndex = i;
                        connectToStart = false;
                    }
                }

                if (bestIndex >= 0)
                {
                    Curve nextCurve = remaining[bestIndex];

                    // If we're connecting to the end point, flip the curve
                    if (!connectToStart)
                    {
                        nextCurve = nextCurve.DuplicateCurve();
                        nextCurve.Reverse();
                    }

                    result.Add(nextCurve);
                    currentPoint = nextCurve.PointAtEnd;
                    remaining.RemoveAt(bestIndex);
                }
                else
                {
                    // This shouldn't happen, but just in case
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Component GUID - required by Grasshopper
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("B5D7E4F8-1A5C-42A1-9D79-E2F3C8D5F610"); }
        }
    }
}