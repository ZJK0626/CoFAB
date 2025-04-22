using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using System.Drawing;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Drawing.Imaging;

namespace CoFab
{
    public class Dalle3GH : GH_Component
    {
        // 异步任务存储变量
        private Task<string> apiTask = null;
        // 存储 API 返回的 JSON 字符串
        private string currentResult = null;
        // 状态信息用于实时反馈
        private string statusMessage = "";

        public Dalle3GH()
          : base("DALL-E 3 Generator",
                 "DALL-E 3",
                 "Call OpenAI DALL-E API, to generate prompt images, use it for subsquent 3D generation.",
                 "CoFab", "AI-assisted 3D Generator")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 文字描述（用户自定义内容）
            pManager.AddTextParameter("Prompt", "Prompt", "Text prompt for image generation", GH_ParamAccess.item);
            // OpenAI API Key
            pManager.AddTextParameter("API Key", "Key", "OpenAI API Key", GH_ParamAccess.item);
            // 指定图像尺寸，可选 1024x1024 / 1024x1792 / 1792x1024
            pManager.AddTextParameter("Size", "Size", "Image Size（DALL·E 3 only support 1024x1024, 1024x1792, 1792x1024）", GH_ParamAccess.item, "1024x1024");
            // quality 参数：standard / hd
            pManager.AddTextParameter("Quality", "Quality", "图像质量（'standard'或'hd'，默认'standard'）", GH_ParamAccess.item, "standard");
            // style 参数：vivid / natural
            pManager.AddTextParameter("Style", "Style", "Image style（'vivid'or 'natural'，default 'vivid'）", GH_ParamAccess.item, "vivid");
            // 运行开关：设置为 true 启动 API 调用
            pManager.AddBooleanParameter("Run", "Run", "Switch of the plugin", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 输出生成的图片临时存储路径
            pManager.AddTextParameter("Image Path", "Path", "The temporary storage path of the generated image（PNG）", GH_ParamAccess.item);
            // 输出实时状态信息
            pManager.AddTextParameter("Status", "Status", "The running status", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string prompt = "";
            string apiKey = "";
            string size = "1024x1024";
            string quality = "standard";
            string style = "vivid";
            bool run = false;

            if (!DA.GetData(0, ref prompt)) return;
            if (!DA.GetData(1, ref apiKey)) return;
            if (!DA.GetData(2, ref size)) return;
            if (!DA.GetData(3, ref quality)) return;
            if (!DA.GetData(4, ref style)) return;
            if (!DA.GetData(5, ref run)) return;

            // 当 Run = false 时，停止执行并清空
            if (!run)
            {
                apiTask = null;
                currentResult = null;
                statusMessage = "Idle";
                DA.SetData(0, "");
                DA.SetData(1, statusMessage);
                return;
            }

            // 当任务尚未创建，则创建一个新的异步任务
            if (apiTask == null)
            {
                statusMessage = "Starting API call...";
                apiTask = CallDalle3ApiAsync(prompt, apiKey, size, quality, style);
                Rhino.RhinoApp.Idle += RhinoApp_Idle;
            }

            // 如果任务已经完成，则处理结果
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

                string imagePath = "";
                if (!string.IsNullOrEmpty(currentResult))
                {
                    try
                    {
                        JObject jsonObj = JObject.Parse(currentResult);

                        // 若返回结果中带有 error 字段，说明 API 报错
                        if (jsonObj["error"] != null)
                        {
                            statusMessage = "API returned error: " + jsonObj["error"].ToString();
                        }
                        else
                        {
                            // 从 data 数组中获取返回的图片数据（base64 编码）
                            JArray dataArray = (JArray)jsonObj["data"];
                            if (dataArray != null && dataArray.Count > 0)
                            {
                                // DALL·E 3 API 中，response_format = "b64_json" 时，图像数据在 b64_json 字段
                                string base64Image = dataArray[0]["b64_json"]?.ToString();
                                if (!string.IsNullOrEmpty(base64Image))
                                {
                                    byte[] imageBytes = Convert.FromBase64String(base64Image);
                                    // 用 Bitmap 读取到内存，然后保存为 .png 文件
                                    using (MemoryStream ms = new MemoryStream(imageBytes))
                                    {
                                        using (Bitmap resultImage = new Bitmap(ms))
                                        {
                                            // 在系统临时目录中存储，用 Guid 保证文件名唯一
                                            imagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".png");
                                            resultImage.Save(imagePath, ImageFormat.Png);
                                        }
                                    }
                                }
                                else
                                {
                                    statusMessage = "No image data found in API response.";
                                }
                            }
                            else
                            {
                                statusMessage = "No image data returned from API.";
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

                // 输出结果
                DA.SetData(0, imagePath);
                DA.SetData(1, statusMessage);

                // 解除 Idle 事件绑定
                Rhino.RhinoApp.Idle -= RhinoApp_Idle;
                apiTask = null;
            }
            else
            {
                // 若任务未完成，依然在运行中
                statusMessage = "API call in progress...";
                DA.SetData(0, "");
                DA.SetData(1, statusMessage);
            }
        }

        // 利用 Rhino 的 Idle 事件周期性刷新组件，实现实时状态更新
        private void RhinoApp_Idle(object sender, EventArgs e)
        {
            ExpireSolution(true);
        }

        /// <summary>
        /// 调用 OpenAI DALL·E 3 API 的异步方法，强制 n=1，并可设置 size、quality、style 等最新参数。
        /// </summary>
        private async Task<string> CallDalle3ApiAsync(string prompt, string apiKey, string size, string quality, string style)
        {

            string endpoint = "https://api.openai.com/v1/images/generations";

            // 如果使用 DALL·E 3，则必须 size ∈ { 1024x1024, 1024x1792, 1792x1024 }，n=1
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);

                // 组装请求体
                // 参考文档：需指定 "model": "dall-e-3"，支持 "size"、"quality"、"style"。
                // n 只能是 1
                var payload = new
                {
                    model = "dall-e-3",
                    prompt = "Create an image at a 45-degree isometric angle. " +
                    "White simple background, no any other elements except the item, no extraneous detail." +
                    "User Description: " + prompt,
                    n = 1,                     // DALL·E 3 仅支持 n=1
                    size = size,               // "1024x1024", "1024x1792", or "1792x1024"
                    response_format = "b64_json",
                    quality = quality,         // "standard" or "hd"
                    style = style              // "vivid" or "natural"
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

        // 如果你想为组件设置自定义图标，可以在此返回一个 Bitmap
        protected override Bitmap Icon => null;

        public override Guid ComponentGuid => new Guid("5E71D2A9-690F-4B4F-9116-94D31B3310E0");
    }
}
