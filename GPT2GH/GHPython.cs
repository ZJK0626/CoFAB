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

namespace GPT2GH
{
    public class PythonCodeGeneratorComponent : GH_Component
    {
        private Task<string> apiTask = null;      // 用于异步请求OpenAI
        private string currentResult = null;      // 存储原始API返回
        private string statusMessage = "";

        public PythonCodeGeneratorComponent()
          : base("Python Code Generator",
                 "PyGen",
                 "Generate Python code (as a string) for Grasshopper Python Script based on a natural language command.",
                 "GPT2GH",
                 "Generation")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 用户在这里输入自然语言指令，如 "Create a 10x10 grid of random spheres"
            pManager.AddTextParameter("Command", "Cmd", "Natural language describing the modeling task", GH_ParamAccess.item);
            // 输入OpenAI的API Key
            pManager.AddTextParameter("API Key", "Key", "OpenAI API Key", GH_ParamAccess.item);
            // 运行开关
            pManager.AddBooleanParameter("Run", "Run", "Set true to call the API and generate the Python code", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 输出Python脚本字符串，可直接复制到GH Python组件中
            pManager.AddTextParameter("Python Code", "Code", "Generated Python script", GH_ParamAccess.item);
            // 输出状态信息
            pManager.AddTextParameter("Status", "Status", "Execution status", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string command = "";
            string apiKey = "";
            bool run = false;

            if (!DA.GetData(0, ref command)) return;
            if (!DA.GetData(1, ref apiKey)) return;
            if (!DA.GetData(2, ref run)) return;

            if (!run)
            {
                // 未运行则重置状态
                apiTask = null;
                currentResult = null;
                statusMessage = "Idle";
                DA.SetData(0, null);
                DA.SetData(1, statusMessage);
                return;
            }

            // 若尚未启动请求，则开始异步调用
            if (apiTask == null)
            {
                statusMessage = "Starting API call...";
                apiTask = CallOpenAIApiAsync(command, apiKey);
                Rhino.RhinoApp.Idle += RhinoApp_Idle;
            }

            // 检查异步任务
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

                // 二次解析：从完整响应中提取content，再解析其中的JSON
                string pythonCode = ParsePythonCodeFromOpenAIResponse(currentResult, ref statusMessage);

                // 将最终的Python脚本输出
                DA.SetData(0, pythonCode);
                DA.SetData(1, statusMessage);

                // 清理
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

        private void RhinoApp_Idle(object sender, EventArgs e)
        {
            ExpireSolution(true);
        }

        /// <summary>
        /// 从OpenAI返回的完整JSON里，先取choices[0].message.content，再解析为JSON，提取"python_code"
        /// 并做简单的markdown清理
        /// </summary>
        private string ParsePythonCodeFromOpenAIResponse(string rawResponse, ref string status)
        {
            if (string.IsNullOrEmpty(rawResponse))
            {
                status = "No response from API.";
                return null;
            }

            try
            {
                // 1) 解析最外层
                JObject root = JObject.Parse(rawResponse);
                string content = root["choices"]?[0]?["message"]?["content"]?.ToString();

                if (string.IsNullOrEmpty(content))
                {
                    status = "No content found in choices[0].message.";
                    return null;
                }

                // 去除可能的三反引号或markdown
                content = RemoveMarkdownCodeFences(content).Trim();

                // 2) 解析content为JSON
                JObject codeJson = JObject.Parse(content);
                string code = codeJson["python_code"]?.ToString();
                if (string.IsNullOrEmpty(code))
                {
                    status = "No 'python_code' field found in content JSON.";
                    return null;
                }

                return code;
            }
            catch (Exception ex)
            {
                status = "Error parsing content JSON: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 简单移除可能的markdown代码块包裹，如```json ... ```或```python ... ```
        /// </summary>
        private string RemoveMarkdownCodeFences(string text)
        {
            text = text.Replace("```json", "").Replace("```python", "").Replace("```", "");
            return text;
        }

        /// <summary>
        /// 异步调用OpenAI接口，要求仅返回一个包含"python_code"字段的JSON
        /// </summary>
        private async Task<string> CallOpenAIApiAsync(string command, string apiKey)
        {
            string endpoint = "https://api.openai.com/v1/chat/completions";

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
                var payload = new
                {
                    model = "gpt-3.5-turbo",
                    messages = new[] {
                        new {
                            role = "system",
                            content =
@"You are a helpful assistant that generates Python code for a Grasshopper Python Script component.
The user provides a modeling command.
Return a JSON object containing only one field 'python_code' with the python script as the value.
No markdown fences, no additional text or explanation."
                        },
                        new {
                            role = "user",
                            content = command
                        }
                    },
                    temperature = 0.0
                };

                string jsonPayload = JsonConvert.SerializeObject(payload);
                StringContent content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                try
                {
                    HttpResponseMessage response = await client.PostAsync(endpoint, content);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync();
                }
                catch (Exception ex)
                {
                    // 若出错，则返回类似 {"error":"..."}
                    return "{\"error\":\"" + ex.Message + "\"}";
                }
            } 
        }

        protected override Bitmap Icon => null;

        public override Guid ComponentGuid => new Guid("E1112223-4444-5555-9999-ABCDEF123457");
    }
}

