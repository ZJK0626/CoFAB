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

namespace GPT2GHModifier
{
    public class GPT2GHModifier : GH_Component
    {
        // 异步任务存储变量
        private Task<string> apiTask = null;
        // 存储 API 返回的 JSON 字符串
        private string currentResult = null;
        // 状态信息用于实时反馈
        private string statusMessage = "";

        public GPT2GHModifier()
          : base("GPT Transformer",
                 "TransformAI",
                 "Use OpenAI to parse transform commands for a Brep and perform the transformation",
                 "GPT2GH",
                 "Generation")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 输入几何体（Brep）
            pManager.AddBrepParameter("Input Brep", "B", "Brep to transform", GH_ParamAccess.item);
            // 自然语言指令，例如“请将几何体沿 X 方向移动 10 个单位，并阵列为 2 行 3 列”
            pManager.AddTextParameter("Command Prompt", "Cmd", "Natural language transform command", GH_ParamAccess.item);
            // OpenAI API Key
            pManager.AddTextParameter("API Key", "Key", "OpenAI API Key", GH_ParamAccess.item);
            // 执行开关：为 true 时启动 API 调用
            pManager.AddBooleanParameter("Run", "Run", "Set true to execute the API call", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 输出变换后的几何体，可能为多个 Brep
            pManager.AddBrepParameter("Transformed Breps", "TB", "Resulting geometry after transformation", GH_ParamAccess.list);
            // 输出实时状态信息
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

        // 利用 Rhino 的 Idle 事件周期性刷新组件，实现实时状态更新
        private void RhinoApp_Idle(object sender, EventArgs e)
        {
            ExpireSolution(true);
        }

        /// <summary>
        /// 异步调用 OpenAI API，将用户的自然语言变换指令转换为 JSON 格式的变换参数
        /// </summary>
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

        /// <summary>
        /// 根据解析出来的变换指令在 RhinoCommon 中对 Brep 执行变换操作
        /// </summary>
        private List<Brep> ExecuteTransforms(Brep inputBrep, TransformInstruction instruction)
        {
            var results = new List<Brep>();

            if (instruction == null)
            {
                // 如果解析失败，则返回原始 Brep
                results.Add(inputBrep.DuplicateBrep());
                return results;
            }

            // 复制初始对象进行操作
            Brep baseBrep = inputBrep.DuplicateBrep();

            // 移动操作
            if (instruction.Operation == "move")
            {
                Vector3d moveVec = new Vector3d(instruction.X, instruction.Y, instruction.Z);
                Transform moveXform = Transform.Translation(moveVec);
                baseBrep.Transform(moveXform);
            }

            // 缩放操作
            if (instruction.Operation == "scale")
            {
                double scaleFactor = instruction.ScaleFactor;
                Point3d center = Point3d.Origin;
                Transform scaleXform = Transform.Scale(center, scaleFactor);
                baseBrep.Transform(scaleXform);
            }

            // 旋转操作
            if (instruction.Operation == "rotate")
            {
                double angleRadians = Math.PI * instruction.Angle / 180.0;
                Vector3d axis = new Vector3d(0, 0, 1);
                Point3d center = Point3d.Origin;
                Transform rotXform = Transform.Rotation(angleRadians, axis, center);
                baseBrep.Transform(rotXform);
            }

            // 阵列操作
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

        public override Guid ComponentGuid => new Guid("A59B753E-1234-4B1F-986B-0A1CE7A4A9EC");
    }

    /// <summary>
    /// 用于序列化 OpenAI 返回的变换指令 JSON 数据
    /// </summary>
    public class TransformInstruction
    {
        public string Operation { get; set; }

        // 移动参数
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        // 缩放参数
        public double ScaleFactor { get; set; }

        // 旋转参数（角度，单位为度）
        public double Angle { get; set; }

        // 阵列参数
        public int ArrayRows { get; set; }
        public int ArrayCols { get; set; }
        public double StepX { get; set; }
        public double StepY { get; set; }
    }
}
