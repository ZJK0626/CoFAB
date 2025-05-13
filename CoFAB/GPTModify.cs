using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Geometry;

namespace CoFab
{
    public class CoFabModifier : GH_Component
    {
        private Task<string> apiTask = null;
        private string currentResult = null;
        private string statusMessage = "";

        public CoFabModifier()
          : base("GPT Modifier",
                 "GPTModifier",
                 "Use OpenAI to parse transform commands for a Brep and perform the transformation",
                 "CoFab",
                 "GPTGeneration")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter("Input Brep", "B", "Brep to transform", GH_ParamAccess.item);
            pManager.AddTextParameter("Command Prompt", "Cmd", "Natural language transform command", GH_ParamAccess.item);
            pManager.AddTextParameter("API Key", "Key", "OpenAI API Key", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Run", "Run", "Set true to execute the API call", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("Transformed Breps", "TB", "Resulting geometry after transformation", GH_ParamAccess.list);
            pManager.AddTextParameter("Status", "Status", "Execution status", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Brep inputBrep = null;
            string commandPrompt = "";
            string apiKey = "";
            bool run = false;

            if (!DA.GetData(0, ref inputBrep)) return;
            if (!DA.GetData(1, ref commandPrompt)) return;
            if (!DA.GetData(2, ref apiKey)) return;
            if (!DA.GetData(3, ref run)) return;

            if (!run)
            {
                apiTask = null;
                currentResult = null;
                statusMessage = "Idle";
                DA.SetDataList(0, null);
                DA.SetData(1, statusMessage);
                return;
            }

            if (apiTask == null)
            {
                statusMessage = "Starting API call...";
                apiTask = CallOpenAIApiAsync(commandPrompt, apiKey);
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

                List<Brep> resultBreps = new List<Brep>();
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
                                TransformInstruction instruction = null;
                                try
                                {
                                    instruction = JsonConvert.DeserializeObject<TransformInstruction>(content);
                                }
                                catch (Exception ex)
                                {
                                    statusMessage = "JSON deserialization error: " + ex.Message;
                                }
                                if (instruction == null)
                                {
                                    statusMessage = "Failed to parse transform instruction.";
                                }
                                else
                                {
                                    resultBreps = ExecuteTransforms(inputBrep, instruction);
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
                DA.SetDataList(0, resultBreps);
                DA.SetData(1, statusMessage);
                Rhino.RhinoApp.Idle -= RhinoApp_Idle;
                apiTask = null;
            }
            else
            {
                statusMessage = "API call in progress...";
                DA.SetDataList(0, null);
                DA.SetData(1, statusMessage);
            }
        }

        private void RhinoApp_Idle(object sender, EventArgs e)
        {
            ExpireSolution(true);
        }

        private async Task<string> CallOpenAIApiAsync(string command, string apiKey)
        {
            string endpoint = "https://api.openai.com/v1/chat/completions";
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
                var payload = new
                {
                    model = "gpt-4",
                    messages = new[] {
                        new { role = "system", content = "你是一个几何变换指令解析器，请将用户的自然语言指令转换为 JSON 格式，JSON 中包含 {\"operation\":\"move|array|scale|rotate\", \"x\":0, \"y\":0, \"z\":0, \"arrayRows\":0, \"arrayCols\":0, \"stepX\":0, \"stepY\":0, \"ScaleFactor\":0, \"Angle\":0} 等结构及具体参数。不要输出额外文字。" },
                        new { role = "user", content = command }
                    },
                    temperature = 0.0,
                    stream = false
                };
                string jsonPayload = JsonConvert.SerializeObject(payload);
                StringContent content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
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


        private List<Brep> ExecuteTransforms(Brep inputBrep, TransformInstruction instruction)
        {
            var results = new List<Brep>();

            if (instruction == null)
            {
                results.Add(inputBrep.DuplicateBrep());
                return results;
            }

            Brep baseBrep = inputBrep.DuplicateBrep();

            if (instruction.Operation == "move")
            {
                Vector3d moveVec = new Vector3d(instruction.X, instruction.Y, instruction.Z);
                Transform moveXform = Transform.Translation(moveVec);
                baseBrep.Transform(moveXform);
            }

            if (instruction.Operation == "scale")
            {
                double scaleFactor = instruction.ScaleFactor;
                Point3d center = Point3d.Origin;
                Transform scaleXform = Transform.Scale(center, scaleFactor);
                baseBrep.Transform(scaleXform);
            }

            if (instruction.Operation == "rotate")
            {
                double angleRadians = Math.PI * instruction.Angle / 180.0;
                Vector3d axis = new Vector3d(0, 0, 1);
                Point3d center = Point3d.Origin;
                Transform rotXform = Transform.Rotation(angleRadians, axis, center);
                baseBrep.Transform(rotXform);
            }

            if (instruction.Operation == "array")
            {
                int rows = instruction.ArrayRows;
                int cols = instruction.ArrayCols;
                double stepX = instruction.StepX;
                double stepY = instruction.StepY;

                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        var copy = baseBrep.DuplicateBrep();
                        Transform arrXform = Transform.Translation(new Vector3d(c * stepX, r * stepY, 0));
                        copy.Transform(arrXform);
                        results.Add(copy);
                    }
                }
                return results;
            }

            results.Add(baseBrep);
            return results;
        }

        protected override Bitmap Icon => null;

        public override Guid ComponentGuid => new Guid("BDAF3D02-48F9-49B3-8946-FC822D9C7156");
    }

    public class TransformInstruction
    {
        public string Operation { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double ScaleFactor { get; set; }
        public double Angle { get; set; }
        public int ArrayRows { get; set; }
        public int ArrayCols { get; set; }
        public double StepX { get; set; }
        public double StepY { get; set; }
    }
}
