import os
import torch

from shap_e.diffusion.sample import sample_latents
from shap_e.diffusion.gaussian_diffusion import diffusion_from_config
from shap_e.models.download import load_model, load_config
from shap_e.util.notebooks import decode_latent_mesh

def main():
    # 如果有 CUDA，则使用 GPU；否则用 CPU
    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print("Using device:", device)

    # 加载 Shap-E 所需模型
    xm = load_model('transmitter', device=device)       # X-Module, 用于解码 3D 网格
    model = load_model('text300M', device=device)       # 文本引导模型
    diffusion = diffusion_from_config(load_config('diffusion'))  # Diffusion 配置

    # 可调参数
    batch_size = 1             # 生成多少个形状（latent）
    guidance_scale = 12.0      # 引导因子，数值越大越贴近文本提示，但可能失真
    prompt = "a mango shape watering can"   # 文本提示

    # 从文本生成潜在表示（Latents）
    latents = sample_latents(
        batch_size=batch_size,
        model=model,
        diffusion=diffusion,
        guidance_scale=guidance_scale,
        model_kwargs=dict(texts=[prompt] * batch_size),
        progress=True,
        clip_denoised=True,
        use_fp16=True,         # 若GPU支持半精度，可加速推理
        use_karras=True,
        karras_steps=72,
        sigma_min=1e-3,
        sigma_max=160,
        s_churn=0,
    )

    # 创建输出网格的文件夹
    os.makedirs("meshes", exist_ok=True)

    # 将每个 latent 解码为网格并保存
    for i, latent in enumerate(latents):
        mesh = decode_latent_mesh(xm, latent).tri_mesh()

        # 保存为 PLY 文件（含顶点、面等）
        ply_path = os.path.join("meshes", f"example_mesh_{i}.ply")
        with open(ply_path, 'wb') as f:
            mesh.write_ply(f)
        print(f"[INFO] Saved PLY => {ply_path}")

        # 也可以同时导出 OBJ 文件
        obj_path = os.path.join("meshes", f"example_mesh_{i}.obj")
        with open(obj_path, 'w') as f:
            mesh.write_obj(f)
        print(f"[INFO] Saved OBJ => {obj_path}")

if __name__ == "__main__":
    main()
