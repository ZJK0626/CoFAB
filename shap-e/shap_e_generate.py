# shap_e_generate.py
import sys, os
import torch
from shap_e.diffusion.sample import sample_latents
from shap_e.diffusion.gaussian_diffusion import diffusion_from_config
from shap_e.models.download import load_model, load_config
from shap_e.util.notebooks import decode_latent_mesh

def main():
    # 解析参数
    if len(sys.argv) < 5:
        print("Usage: python shap_e_generate.py <prompt> <guidance_scale> <steps> <output_obj>")
        sys.exit(1)

    prompt = sys.argv[1]
    guidance_scale = float(sys.argv[2])
    steps = int(sys.argv[3])
    output_obj = sys.argv[4]

    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    xm = load_model('transmitter', device=device)
    model = load_model('text300M', device=device)
    diffusion = diffusion_from_config(load_config('diffusion'))

    latents = sample_latents(
        batch_size=1,
        model=model,
        diffusion=diffusion,
        guidance_scale=guidance_scale,
        model_kwargs=dict(texts=[prompt]),
        progress=True,
        clip_denoised=True,
        use_fp16=True,
        use_karras=True,
        karras_steps=steps,
        sigma_min=1e-3,
        sigma_max=160,
        s_churn=0,
    )

    mesh = decode_latent_mesh(xm, latents[0]).tri_mesh()
    with open(output_obj, 'w') as f:
        mesh.write_obj(f)
    print(f"Shap-E generation done, saved => {output_obj}")

if __name__ == "__main__":
    main()
