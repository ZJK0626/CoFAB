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
        private Task<string> apiTask = null;
        private string currentResult = null;
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
            pManager.AddTextParameter("Prompt", "Prompt", "Text prompt for image generation", GH_ParamAccess.item);
            pManager.AddTextParameter("API Key", "Key", "OpenAI API Key", GH_ParamAccess.item);
            pManager.AddTextParameter("Size", "Size", "Image Size（DALL·E 3 only support 1024x1024, 1024x1792, 1792x1024）", GH_ParamAccess.item, "1024x1024");
            pManager.AddTextParameter("Quality", "Quality", "Image Quality（'standard'or 'hd'，default 'standard'）", GH_ParamAccess.item, "standard");
            pManager.AddTextParameter("Style", "Style", "Image style（'vivid'or 'natural'，default 'vivid'）", GH_ParamAccess.item, "vivid");
            pManager.AddBooleanParameter("Run", "Run", "Switch of the plugin", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Image Path", "Path", "The temporary storage path of the generated image（PNG）", GH_ParamAccess.item);
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

            if (!run)
            {
                apiTask = null;
                currentResult = null;
                statusMessage = "Idle";
                DA.SetData(0, "");
                DA.SetData(1, statusMessage);
                return;
            }

            if (apiTask == null)
            {
                statusMessage = "Starting API call...";
                apiTask = CallDalle3ApiAsync(prompt, apiKey, size, quality, style);
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

                string imagePath = "";
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
                            JArray dataArray = (JArray)jsonObj["data"];
                            if (dataArray != null && dataArray.Count > 0)
                            {

                                string base64Image = dataArray[0]["b64_json"]?.ToString();
                                if (!string.IsNullOrEmpty(base64Image))
                                {
                                    byte[] imageBytes = Convert.FromBase64String(base64Image);
                                    using (MemoryStream ms = new MemoryStream(imageBytes))
                                    {
                                        using (Bitmap resultImage = new Bitmap(ms))
                                        {
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
                DA.SetData(0, imagePath);
                DA.SetData(1, statusMessage);

                Rhino.RhinoApp.Idle -= RhinoApp_Idle;
                apiTask = null;
            }
            else
            {
                statusMessage = "API call in progress...";
                DA.SetData(0, "");
                DA.SetData(1, statusMessage);
            }
        }

        private void RhinoApp_Idle(object sender, EventArgs e)
        {
            ExpireSolution(true);
        }


        private async Task<string> CallDalle3ApiAsync(string prompt, string apiKey, string size, string quality, string style)
        {

            string endpoint = "https://api.openai.com/v1/images/generations";

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);

                var payload = new
                {
                    model = "dall-e-3",
                    prompt = "Create an image at a 45-degree isometric angle. " +
                    "White simple background, no any other elements except the item, no extraneous detail." +
                    "User Description: " + prompt,
                    n = 1,                  
                    size = size,              
                    response_format = "b64_json",
                    quality = quality,         
                    style = style              
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
        protected override Bitmap Icon => null;

        public override Guid ComponentGuid => new Guid("5E71D2A9-690F-4B4F-9116-94D31B3310E0");
    }
}
