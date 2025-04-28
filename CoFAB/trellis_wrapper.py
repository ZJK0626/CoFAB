"""
TRELLIS Wrapper Script for Grasshopper
This script ensures the correct environment is set up before running TRELLIS
"""

import os
import sys
import argparse
import traceback

# Add the TRELLIS directory to Python path
# Change this to match your actual TRELLIS installation directory
TRELLIS_DIR = os.path.dirname(os.path.abspath(__file__))
if TRELLIS_DIR not in sys.path:
    sys.path.insert(0, TRELLIS_DIR)

# Set environment variables
os.environ['SPCONV_ALGO'] = 'native'
# If you're using xformers instead of flash_attn:
# os.environ['ATTN_BACKEND'] = 'xformers'

def main():
    # Setup detailed error reporting
    try:
        # Print environment info for debugging
        print("Python executable:", sys.executable)
        print("Python version:", sys.version)
        print("Current working directory:", os.getcwd())
        print("PYTHONPATH:", sys.path)
        
        # Parse command line arguments
        parser = argparse.ArgumentParser(description='Generate 3D model using TRELLIS')
        parser.add_argument('--mode', type=str, choices=['image', 'text'], default='image',
                            help='Mode: "image" for image-to-3D or "text" for text-to-3D')
        parser.add_argument('--input', type=str, required=True,
                            help='Path to input image for image mode, or text prompt for text mode')
        parser.add_argument('--output_dir', type=str, required=True,
                            help='Directory to save output files')
        parser.add_argument('--seed', type=int, default=0,
                            help='Random seed for generation')
        parser.add_argument('--ss_steps', type=int, default=12,
                            help='Sampling steps for sparse structure generation')
        parser.add_argument('--ss_guidance', type=float, default=7.5,
                            help='Guidance strength for sparse structure generation')
        parser.add_argument('--slat_steps', type=int, default=12,
                            help='Sampling steps for structured latent generation')
        parser.add_argument('--slat_guidance', type=float, default=3.0,
                            help='Guidance strength for structured latent generation')
        
        args = parser.parse_args()
        
        # Create output directory if it doesn't exist
        os.makedirs(args.output_dir, exist_ok=True)
        
        # IMPORTANT: Try to import TRELLIS modules, with detailed error reporting
        try:
            print("Attempting to import TRELLIS modules...")
            import torch
            print(f"PyTorch version: {torch.__version__}")
            print(f"CUDA available: {torch.cuda.is_available()}")
            if torch.cuda.is_available():
                print(f"CUDA device: {torch.cuda.get_device_name(0)}")
            
            from trellis.pipelines import TrellisImageTo3DPipeline
            from trellis.utils import render_utils, postprocessing_utils
            print("TRELLIS modules imported successfully!")
        except ImportError as e:
            print(f"ERROR: Failed to import TRELLIS modules: {e}")
            print("\nDetailed traceback:")
            traceback.print_exc()
            print("\nPossible solutions:")
            print("1. Make sure TRELLIS is installed in this Python environment")
            print("2. Run 'pip install -e .' in the TRELLIS directory")
            print("3. Check that all TRELLIS dependencies are installed")
            print("4. Try installing TRELLIS with 'pip install git+https://github.com/sdbds/TRELLIS-for-windows.git'")
            sys.exit(1)
        
        # Continue with TRELLIS generation
        if args.mode == "image":
            pipeline = TrellisImageTo3DPipeline.from_pretrained("JeffreyXiang/TRELLIS-image-large")
            pipeline.cuda()
            
            # Load the input image
            if not os.path.exists(args.input):
                print(f"ERROR: Input image not found: {args.input}")
                sys.exit(1)
                
            from PIL import Image
            image = Image.open(args.input)
            
            # Run the pipeline
            outputs = pipeline.run(
                image,
                seed=args.seed,
                sparse_structure_sampler_params={
                    "steps": args.ss_steps,
                    "cfg_strength": args.ss_guidance,
                },
                slat_sampler_params={
                    "steps": args.slat_steps,
                    "cfg_strength": args.slat_guidance,
                },
            )
        else:  # text mode
            print("ERROR: Text-to-3D mode is not yet implemented")
            sys.exit(1)
        
        # Save the outputs
        import imageio
        import trimesh
        import numpy as np
        
        obj_path = os.path.join(args.output_dir, "model.obj")
        ply_path = os.path.join(args.output_dir, "gaussian.ply")
        glb_path = os.path.join(args.output_dir, "model.glb")
        preview_path = os.path.join(args.output_dir, "preview.mp4")
        
        # Save mesh as OBJ using trimesh
        try:
            # First convert to GLB with texture
            glb = postprocessing_utils.to_glb(
                outputs['gaussian'][0],
                outputs['mesh'][0],
                simplify=0.95,
                texture_size=1024,
            )
            glb.export(glb_path)
            
            # Export OBJ by creating a trimesh from vertices and faces
            mesh = outputs['mesh'][0]
            vertices = mesh.vertices.cpu().numpy()
            faces = mesh.faces.cpu().numpy()
            
            # Create a trimesh object
            mesh_obj = trimesh.Trimesh(vertices=vertices, faces=faces)
            mesh_obj.export(obj_path)
            
        except Exception as e:
            print(f"ERROR: Failed to save outputs: {str(e)}")
            traceback.print_exc()
            sys.exit(1)
            
    except Exception as e:
        print(f"ERROR: An unexpected error occurred: {str(e)}")
        traceback.print_exc()
        sys.exit(1)
    
    # Clean up
    import torch
    torch.cuda.empty_cache()
    
if __name__ == "__main__":
    main()