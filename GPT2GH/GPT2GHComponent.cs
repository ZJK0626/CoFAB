using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.Geometry;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;          // 用于 JSON 序列化
using Newtonsoft.Json.Linq;     // 用于 JSON 解析
using System.Text.RegularExpressions; // 用于正则表达式解析

namespace GPT2GH
{
    public class GPT2GH : GH_Component
    {
        // 异步任务存储变量
        private Task<string> apiTask = null;
        // 存储 API 返回的 JSON 字符串
        private string currentResult = null;
        // 状态信息用于实时反馈
        private string statusMessage = "";

        public GPT2GH()
          : base("GPT2GH Generator",
                 "GPT",
                 "Generate basic solids via natural language instructions using the OpenAI API.",
                 "GPT2GH", "Generation")
        {
        }


        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 英文指令，例如 "I want a box with 10 cm x 10cm x 10cm"
            pManager.AddTextParameter("Command", "Cmd", "Natural language command in English", GH_ParamAccess.item);
            // OpenAI API Key
            pManager.AddTextParameter("API Key", "Key", "OpenAI API Key", GH_ParamAccess.item);
            // Run switch: set true to start API call
            pManager.AddBooleanParameter("Run", "Run", "Set true to execute the API call", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 输出生成的几何体（Brep）
            pManager.AddBrepParameter("Geometry", "Geo", "Generated geometry", GH_ParamAccess.item);
            // 输出实时状态信息
            pManager.AddTextParameter("Status", "Status", "Execution status", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // 获取输入参数
            string command = "";
            string apiKey = "";
            bool run = false;
            if (!DA.GetData(0, ref command)) return;
            if (!DA.GetData(1, ref apiKey)) return;
            if (!DA.GetData(2, ref run)) return;

            if (!run)
            {
                apiTask = null;
                currentResult = null;
                statusMessage = "Idle";
                DA.SetData(0, null);
                DA.SetData(1, statusMessage);
                return;
            }

            if (apiTask == null)
            {
                statusMessage = "Starting API call...";
                apiTask = CallOpenAIApiAsync(command, apiKey);
                Rhino.RhinoApp.Idle += RhinoApp_Idle;
            }

            if (apiTask.IsCompleted)
            {
                if (apiTask.Exception != null)
                {
                    statusMessage = "Error: " + apiTask.Exception.InnerException.Message;
                }
                else
                {
                    currentResult = apiTask.Result;
                    statusMessage = "API call completed.";
                }

                Brep resultBrep = null;
                if (!string.IsNullOrEmpty(currentResult))
                {
                    try
                    {
                        JObject jsonObj = JObject.Parse(currentResult);
                        if (jsonObj["error"] != null)
                        {
                            statusMessage = "API returned error: " + jsonObj["error"].ToString();
                        }
                        else
                        {
                            // 从 choices 数组中获取 message.content
                            JArray choices = (JArray)jsonObj["choices"];
                            string content = "";
                            if (choices != null && choices.Count > 0)
                            {
                                var message = choices[0]["message"];
                                if (message != null && message["content"] != null)
                                    content = message["content"].ToString();
                            }
                            if (string.IsNullOrEmpty(content))
                            {
                                statusMessage = "No valid content found in JSON.";
                            }
                            else
                            {
                                // 使用辅助函数识别形状
                                string detectedShape = DetectShape(content);
                                if (string.IsNullOrEmpty(detectedShape))
                                {
                                    statusMessage = "Could not detect shape type from response. Response: " + content;
                                }
                                else if (detectedShape == "pyramid")
                                {
                                    bool parsedStructured = false;
                                    double? heightVal = null, baseSideVal = null;
                                    try
                                    {
                                        JObject structured = JObject.Parse(content);
                                        if (structured["shape"] != null && structured["shape"].ToString().ToLower() == "pyramid")
                                        {
                                            heightVal = structured["height"].Value<double>();
                                            baseSideVal = structured["baseSide"].Value<double>();
                                            parsedStructured = true;
                                        }
                                    }
                                    catch { }
                                    if (!parsedStructured)
                                    {
                                        heightVal = ExtractDimension(content, new string[] { "height", "hight", "heigt" });
                                        baseSideVal = ExtractDimension(content, new string[] { "base side length", "base edge length", "side length" });
                                    }
                                    if (heightVal.HasValue && baseSideVal.HasValue)
                                    {
                                        double half = baseSideVal.Value / 2.0;
                                        Point3d pt0 = new Point3d(-half, -half, 0);
                                        Point3d pt1 = new Point3d(half, -half, 0);
                                        Point3d pt2 = new Point3d(half, half, 0);
                                        Point3d pt3 = new Point3d(-half, half, 0);
                                        Polyline basePoly = new Polyline(new List<Point3d> { pt0, pt1, pt2, pt3, pt0 });
                                        Curve baseCurve = basePoly.ToNurbsCurve();
                                        Point3d apex = new Point3d(0, 0, heightVal.Value);
                                        List<Brep> sideBreps = new List<Brep>();
                                        for (int i = 0; i < 4; i++)
                                        {
                                            Point3d ptA = basePoly[i];
                                            Point3d ptB = basePoly[i + 1];
                                            Brep triBrep = Brep.CreateFromCornerPoints(ptA, ptB, apex, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);
                                            if (triBrep != null)
                                                sideBreps.Add(triBrep);
                                        }
                                        Brep[] baseBreps = Brep.CreatePlanarBreps(new List<Curve> { baseCurve }, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);
                                        if (baseBreps != null && baseBreps.Length > 0)
                                        {
                                            List<Brep> allBreps = new List<Brep>();
                                            allBreps.Add(baseBreps[0]);
                                            allBreps.AddRange(sideBreps);
                                            Brep[] joined = Brep.JoinBreps(allBreps, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);
                                            if (joined != null && joined.Length > 0)
                                                resultBrep = joined[0];
                                        }
                                    }
                                    else
                                    {
                                        statusMessage = "Could not extract parameters for pyramid from response: " + content;
                                    }
                                }
                                else if (detectedShape == "cube")
                                {
                                    bool parsedStructured = false;
                                    double? sideVal = null;
                                    try
                                    {
                                        JObject structured = JObject.Parse(content);
                                        if (structured["shape"] != null && structured["shape"].ToString().ToLower() == "cube")
                                        {
                                            sideVal = structured["side"].Value<double>();
                                            parsedStructured = true;
                                        }
                                    }
                                    catch { }
                                    if (!parsedStructured)
                                    {
                                        // 尝试匹配 "dimensions of 10 cm x 10 cm x 10 cm" 格式
                                        Match dimsMatch = Regex.Match(content, @"dimensions\s*of\s*(\d+(\.\d+)?)\s*cm\s*[x×]\s*(\d+(\.\d+)?)\s*cm\s*[x×]\s*(\d+(\.\d+)?)\s*cm", RegexOptions.IgnoreCase);
                                        if (dimsMatch.Success)
                                        {
                                            double d1 = double.Parse(dimsMatch.Groups[1].Value);
                                            double d2 = double.Parse(dimsMatch.Groups[3].Value);
                                            double d3 = double.Parse(dimsMatch.Groups[5].Value);
                                            if (Math.Abs(d1 - d2) < 0.001 && Math.Abs(d2 - d3) < 0.001)
                                            {
                                                sideVal = d1;
                                            }
                                            else
                                            {
                                                // 如果三个尺寸不相等，则视为 cuboid
                                                double halfL = d1 / 2.0;
                                                double halfW = d2 / 2.0;
                                                double halfH = d3 / 2.0;
                                                Point3d minPt = new Point3d(-halfL, -halfW, -halfH);
                                                Point3d maxPt = new Point3d(halfL, halfW, halfH);
                                                Box cuboid = new Box(new BoundingBox(minPt, maxPt));
                                                resultBrep = cuboid.ToBrep();
                                                parsedStructured = true;
                                            }
                                        }
                                        else
                                        {
                                            sideVal = ExtractDimension(content, new string[] { "side length", "edge length" });
                                        }
                                    }
                                    if (resultBrep == null && sideVal.HasValue)

                                    {
                                        double half = sideVal.Value / 2.0;
                                        Point3d minPt = new Point3d(-half, -half, -half);
                                        Point3d maxPt = new Point3d(half, half, half);
                                        Box cube = new Box(new BoundingBox(minPt, maxPt));
                                        resultBrep = cube.ToBrep();
                                    }
                                    else if (resultBrep == null)
                                    {
                                        statusMessage = "Could not extract parameters for cube from response: " + content;
                                    }
                                }
                                else if (detectedShape == "cylinder")
                                {
                                    bool parsedStructured = false;
                                    double? radiusVal = null, heightVal = null;
                                    try
                                    {
                                        JObject structured = JObject.Parse(content);
                                        if (structured["shape"] != null && structured["shape"].ToString().ToLower() == "cylinder")
                                        {
                                            radiusVal = structured["radius"].Value<double>();
                                            heightVal = structured["height"].Value<double>();
                                            parsedStructured = true;
                                        }
                                    }
                                    catch { }
                                    if (!parsedStructured)
                                    {
                                        radiusVal = ExtractDimension(content, new string[] { "radius" });
                                        heightVal = ExtractDimension(content, new string[] { "height", "hight" });
                                    }
                                    if (radiusVal.HasValue && heightVal.HasValue)
                                    {
                                        Circle circle = new Circle(Point3d.Origin, radiusVal.Value);
                                        Cylinder cylinder = new Cylinder(circle, heightVal.Value);
                                        resultBrep = cylinder.ToBrep(true, true);
                                    }
                                    else
                                    {
                                        statusMessage = "Could not extract parameters for cylinder from response: " + content;
                                    }
                                }
                                else if (detectedShape == "cone")
                                {
                                    bool parsedStructured = false;
                                    double? baseRadiusVal = null, heightVal = null;
                                    try
                                    {
                                        JObject structured = JObject.Parse(content);
                                        if (structured["shape"] != null && structured["shape"].ToString().ToLower() == "cone")
                                        {
                                            baseRadiusVal = structured["baseRadius"].Value<double>();
                                            heightVal = structured["height"].Value<double>();
                                            parsedStructured = true;
                                        }
                                    }
                                    catch { }
                                    if (!parsedStructured)
                                    {
                                        baseRadiusVal = ExtractDimension(content, new string[] { "base radius" });
                                        heightVal = ExtractDimension(content, new string[] { "height", "hight" });
                                    }
                                    if (baseRadiusVal.HasValue && heightVal.HasValue)
                                    {
                                        Plane basePlane = Plane.WorldXY;
                                        Cone cone = new Cone(basePlane, heightVal.Value, baseRadiusVal.Value);
                                        resultBrep = cone.ToBrep(true);
                                    }
                                    else
                                    {
                                        statusMessage = "Could not extract parameters for cone from response: " + content;
                                    }
                                }
                                else if (detectedShape == "sphere")
                                {
                                    bool parsedStructured = false;
                                    double? radiusVal = null;
                                    try
                                    {
                                        JObject structured = JObject.Parse(content);
                                        if (structured["shape"] != null && structured["shape"].ToString().ToLower() == "sphere")
                                        {
                                            radiusVal = structured["radius"].Value<double>();
                                            parsedStructured = true;
                                        }
                                    }
                                    catch { }
                                    if (!parsedStructured)
                                    {
                                        radiusVal = ExtractDimension(content, new string[] { "radius" });
                                    }
                                    if (radiusVal.HasValue)
                                    {
                                        Sphere sphere = new Sphere(Point3d.Origin, radiusVal.Value);
                                        resultBrep = sphere.ToBrep();
                                    }
                                    else
                                    {
                                        statusMessage = "Could not extract parameters for sphere from response: " + content;
                                    }
                                }
                                else if (detectedShape == "cuboid")
                                {
                                    bool parsedStructured = false;
                                    double? lengthVal = null, widthVal = null, heightVal = null;
                                    try
                                    {
                                        JObject structured = JObject.Parse(content);
                                        if (structured["shape"] != null && structured["shape"].ToString().ToLower() == "cuboid")
                                        {
                                            lengthVal = structured["length"].Value<double>();
                                            widthVal = structured["width"].Value<double>();
                                            heightVal = structured["height"].Value<double>();
                                            parsedStructured = true;
                                        }
                                    }
                                    catch { }
                                    if (!parsedStructured)
                                    {
                                        lengthVal = ExtractDimension(content, new string[] { "length" });
                                        widthVal = ExtractDimension(content, new string[] { "width" });
                                        heightVal = ExtractDimension(content, new string[] { "height", "hight" });
                                    }
                                    if (lengthVal.HasValue && widthVal.HasValue && heightVal.HasValue)
                                    {
                                        double halfL = lengthVal.Value / 2.0;
                                        double halfW = widthVal.Value / 2.0;
                                        double halfH = heightVal.Value / 2.0;
                                        Point3d minPt = new Point3d(-halfL, -halfW, -halfH);
                                        Point3d maxPt = new Point3d(halfL, halfW, halfH);
                                        Box cuboid = new Box(new BoundingBox(minPt, maxPt));
                                        resultBrep = cuboid.ToBrep();
                                    }
                                    else
                                    {
                                        statusMessage = "Could not extract parameters for cuboid from response: " + content;
                                    }
                                }
                                else
                                {
                                    statusMessage = "Unsupported solid type. Please use pyramid, cube, cylinder, cone, sphere, or cuboid.";
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        statusMessage = "JSON parsing error: " + ex.Message;
                    }
                }
                else
                {
                    statusMessage = "No result returned from API.";
                }
                DA.SetData(0, resultBrep);
                DA.SetData(1, statusMessage);
                Rhino.RhinoApp.Idle -= RhinoApp_Idle;
                apiTask = null;
            }
            else
            {
                statusMessage = "API call in progress...";
                DA.SetData(0, null);
                DA.SetData(1, statusMessage);
            }
        }

        // 利用 Rhino 的 Idle 事件周期性刷新组件，实现实时状态更新
        private void RhinoApp_Idle(object sender, EventArgs e)
        {
            ExpireSolution(true);
        }

        /// <summary>
        /// 使用 HttpClient 异步调用 OpenAI API，并采用 Newtonsoft.Json 序列化请求数据。
        /// 系统提示明确要求返回仅包含必要参数的结构化 JSON 对象，用于 Grasshopper 模型生成.
        /// </summary>
        private async Task<string> CallOpenAIApiAsync(string command, string apiKey)
        {
            string endpoint = "https://api.openai.com/v1/chat/completions";
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
                var payload = new
                {
                    model = "gpt-4", // or "gpt-4"
                    messages = new[] {
                        new { role = "system", content = "You are a helpful assistant specialized in Grasshopper geometry generation. The user will provide a natural language command to generate a model in Grasshopper. Please analyze the command and return only a structured JSON object with the necessary parameters for the requested model, using the following formats: For a pyramid, return: { \"shape\": \"pyramid\", \"height\": <number>, \"baseSide\": <number> }. For a cube, return: { \"shape\": \"cube\", \"side\": <number> }. For a cylinder, return: { \"shape\": \"cylinder\", \"radius\": <number>, \"height\": <number> }. For a cone, return: { \"shape\": \"cone\", \"baseRadius\": <number>, \"height\": <number> }. For a sphere, return: { \"shape\": \"sphere\", \"radius\": <number> }. For a cuboid, return: { \"shape\": \"cuboid\", \"length\": <number>, \"width\": <number>, \"height\": <number> }. Do not include any additional text, explanations, or formatting." },
                        new { role = "user", content = command }
                    },
                    temperature = 0.7,
                    stream = false
                };
                string jsonPayload = JsonConvert.SerializeObject(payload);
                StringContent content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
                try
                {
                    HttpResponseMessage response = await client.PostAsync(endpoint, content);
                    response.EnsureSuccessStatusCode();
                    string responseString = await response.Content.ReadAsStringAsync();
                    return responseString;
                }
                catch (Exception ex)
                {
                    return "{\"error\":\"" + ex.Message + "\"}";
                }
            }
        }

        /// <summary>
        /// 基于关键词列表提取尺寸信息。返回数字（double）或 null。
        /// </summary>
        private double? ExtractDimension(string content, string[] keywords)
        {
            foreach (var keyword in keywords)
            {
                string pattern = keyword + @"\s*(of)?\s*(\d+(\.\d+)?)\s*cm";
                Match match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    double val;
                    if (double.TryParse(match.Groups[2].Value, out val))
                        return val;
                }
            }
            return null;
        }

        /// <summary>
        /// 根据输入文本模糊识别几何体类型，支持常见同义词和拼写错误
        /// </summary>
        private string DetectShape(string text)
        {
            string lower = text.ToLower();
            if (lower.Contains("pyramid") || lower.Contains("piramid"))
                return "pyramid";
            if (lower.Contains("cube") || lower.Contains("box") || lower.Contains("hexahedron"))
                return "cube";
            if (lower.Contains("cylinder") || lower.Contains("tube"))
                return "cylinder";
            if (lower.Contains("cone") || lower.Contains("conical"))
                return "cone";
            if (lower.Contains("sphere") || lower.Contains("ball") || lower.Contains("orb"))
                return "sphere";
            if (lower.Contains("cuboid") || lower.Contains("rectangular prism"))
                return "cuboid";
            return null;
        }

        protected override System.Drawing.Bitmap Icon => null; // You may add a custom icon here

        public override Guid ComponentGuid => new Guid("3c07a6f2-2b2e-4a6c-9f7b-0e1d3f4c5b2e");

        public static object Properties { get; internal set; }
    }
}
