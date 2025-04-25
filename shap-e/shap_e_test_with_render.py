import os
import torch
from PIL import Image

# Shap-E 相关导入
from shap_e.diffusion.sample import sample_latents
from shap_e.diffusion.gaussian_diffusion import diffusion_from_config
from shap_e.models.download import load_model, load_config
from shap_e.util.notebooks import (
    create_pan_cameras,
    decode_latent_images,
    decode_latent_mesh
)

def save_images_as_gif(images, filename, duration=100):
    """
    images: 列表，每个元素是 PIL.Image 对象
    filename: 输出的 GIF 文件名（带 .gif 后缀）
    duration: 每帧显示时间（毫秒）
    """
    # 确保所有图像为 RGB 模式
    rgb_images = []
    for img in images:
        if img.mode != 'RGB':
            rgb_images.append(img.convert('RGB'))
        else:
            rgb_images.append(img)
    # 用 Pillow 将多张图像合成为 GIF
    rgb_images[0].save(
        filename,
        save_all=True,
        append_images=rgb_images[1:],
        duration=duration,
        loop=0
    )

def main():
    # 如果有 CUDA，则使用 GPU；否则用 CPU
    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print("Using device:", device)

    # 加载 Shap-E 所需模型
    xm = load_model('transmitter', device=device)       # X-Module, 用于解码渲染
    model = load_model('text300M', device=device)       # 文字引导模型
    diffusion = diffusion_from_config(load_config('diffusion'))  # Diffusion 配置

    # 一些可调参数
    batch_size = 2             # 生成多少个latent
    guidance_scale = 15.0      # 越大生成效果越强，可能失真
    prompt = "a beer bottle"         # 文字提示

    # 从文本生成潜在表示（Latents）
    latents = sample_latents(
        batch_size=batch_size,
        model=model,
        diffusion=diffusion,
        guidance_scale=guidance_scale,
        model_kwargs=dict(texts=[prompt] * batch_size),
        progress=True,
        clip_denoised=True,
        use_fp16=True,         # 若GPU支持半精度，可加速
        use_karras=True,
        karras_steps=48,
        sigma_min=1e-3,
        sigma_max=160,
        s_churn=0,
    )

    # 指定渲染模式 ('nerf' 或 'stf') 以及渲染图像大小
    render_mode = 'nerf'
    render_size = 64
    cameras = create_pan_cameras(render_size, device)

    # 创建输出目录
    os.makedirs("renders", exist_ok=True)
    os.makedirs("meshes", exist_ok=True)

    # 对生成的每个 latent 进行渲染并导出网格
    for i, latent in enumerate(latents):
        # 使用解码器渲染多视角图像
        images = decode_latent_images(
            xm, 
            latent, 
            cameras, 
            rendering_mode=render_mode
        )

        # 保存 GIF 动画
        gif_path = os.path.join("renders", f"latent_{i}.gif")
        save_images_as_gif(images, gif_path, duration=120)
        print(f"[INFO] Saved GIF => {gif_path}")

        # 解码为 3D 网格并保存为 PLY/OBJ
        mesh = decode_latent_mesh(xm, latent).tri_mesh()
        ply_path = os.path.join("meshes", f"example_mesh_{i}.ply")
        obj_path = os.path.join("meshes", f"example_mesh_{i}.obj")

        with open(ply_path, 'wb') as f:
            mesh.write_ply(f)
        with open(obj_path, 'w') as f:
            mesh.write_obj(f)
        print(f"[INFO] Saved Mesh => {ply_path} & {obj_path}")

if __name__ == "__main__":
    main()
