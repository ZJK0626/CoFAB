# CoFAB: AI-Collaboration System for 3D Model Generation and Rapid Prototyping

CoFAB is a Grasshopper plugin for Rhino that bridges the gap between artificial intelligence and digital fabrication. This toolkit enables designers to generate, modify, and prepare 3D models for fabrication using state-of-the-art AI models and natural language processing.

*This project is part of Junke Zhao's thesis for the Master of Science in Computational Design at Carnegie Mellon University.*

## Features

CoFAB provides a comprehensive set of tools organized into three main categories:

### 1. AI Model Generation and Modification

- **DALL-E 3 Generator**: Generate concept images from text prompts using OpenAI's DALL-E 3, which can serve as inspiration or references for 3D modeling.
- **ShapE Generator**: Create 3D models directly from text descriptions using the Shap-E AI model.
- **TRELLIS 3D Generator**: Generate detailed 3D models from either images or text using the TRELLIS architecture.
- **GPT Modifier**: Transform 3D models using natural language commands (move, scale, rotate, array) interpreted by GPT-4.

### 2. Digital Fabrication Preparation

- **Wireframer**: Convert meshes into printable wireframe structures with self-supporting properties, optimized for additive manufacturing.
- **GCode Generation**: Transform curved geometries into G-code for robotic arm 3D printers and CNC machines, with support for various printing parameters.

### 3. Robotic Fabrication

- **Robotic Arm Communication**: Tools for direct communication with robotic fabrication systems.

## Installation

1. **Prerequisites**:
   - Rhino 8 (Windows or Mac)
   - Grasshopper for Rhino 8
   - .NET Framework 4.8 or higher

2. **Install via Package Manager**:
   - Open Rhino and type `PackageManager` in the command line
   - Search for "CoFAB" and click Install

3. **Manual Installation**:
   - Download the latest release from the repository
   - Unzip the file
   - Copy the `CoFab.gha` file to your Grasshopper Components folder:
     - Windows: `%APPDATA%\Grasshopper\Libraries`
     - Mac: `~/Library/Application Support/Grasshopper/Libraries`

4. **External Dependencies**:
   - For AI components, you'll need:
     - OpenAI API key for DALL-E and GPT components
     - Python environment with Shap-E installed for the ShapE Generator
     - TRELLIS environment for the TRELLIS Generator

## Component Documentation

### DALL-E 3 Generator
Generates images from text prompts using OpenAI's DALL-E 3 API.

**Inputs**:
- Prompt: Text description of the desired image
- API Key: OpenAI API key
- Size: Image size (1024x1024, 1024x1792, or 1792x1024)
- Quality: Image quality (standard or hd)
- Style: Image style (vivid or natural)
- Run: Set to true to execute

**Outputs**:
- Image Path: Path to the generated image
- Status: Operation status information

### ShapE Generator
Generates 3D models from text descriptions using Shap-E AI.

**Inputs**:
- Prompt: Text description of the desired 3D model
- GuidanceScale: Controls how closely the output follows the prompt
- KarrasSteps: Number of sampling steps
- Run: Set to true to execute
- Remesh: Apply QuadRemesh to the output
- TargetQuads: Target number of quads for remeshing

**Outputs**:
- Mesh: Generated 3D mesh
- ConsoleLog: Process output information

### TRELLIS 3D Generator
Generates detailed 3D models from images or text using the TRELLIS architecture.

**Inputs**:
- Mode: "image" or "text"
- Input: Image file path or text prompt
- Seed: Random seed for generation
- Output Directory: Where to save the outputs
- Advanced parameters for generation control
- Run: Set to true to execute

**Outputs**:
- Mesh: Generated 3D mesh
- GLB Path: Path to the textured model
- PLY Path: Path to the Gaussian PLY file
- Preview: Path to the preview video

### GPT Modifier
Transforms 3D models using natural language commands interpreted by GPT-4.

**Inputs**:
- Input Brep: Geometry to transform
- Command Prompt: Natural language instruction for transformation
- API Key: OpenAI API key
- Run: Set to true to execute

**Outputs**:
- Transformed Breps: Resulting geometries
- Status: Operation status information

### Wireframer
Transforms meshes into printable wireframe structures with self-supporting properties.

**Inputs**:
- Mesh: Input mesh to transform
- ContourSpacing: Distance between contour slices
- SupportAngle: Maximum overhang angle
- TargetEdgeCount: Target number of edges
- OptimizeForPrinting: Apply self-supporting optimization
- Various parameters for wireframe customization

**Outputs**:
- WireframeMesh: Resulting wireframe mesh
- Contours: Extracted contour lines
- PillarEdges: Vertical connection elements
- Stats: Structure statistics

### GCode Generation
Converts curves to G-code for robotic arm 3D printers and CNC machines.

**Inputs**:
- Curves: Input geometries to convert
- Print Speed/Travel Speed: Movement speeds
- Z Height/Safe Z: Working heights
- Print Temperature: Hotend temperature
- Various 3D printing parameters
- Path settings and execution controls

**Outputs**:
- G-code: Generated instruction text
- Tool Path: Ordered path for preview
- Status: Operation information

## Requirements

- **Software**:
  - Rhino 8
  - Grasshopper for Rhino 8
  - .NET Framework 4.8+

- **Hardware**:
  - Recommended: 16GB+ RAM, dedicated GPU
  - Required for real-time AI: CUDA-compatible GPU with 8GB+ VRAM

- **External Services**:
  - OpenAI API key for DALL-E and GPT components
  - Python 3.8+ with specific packages for Shap-E and TRELLIS

## Example Workflows

1. **Text-to-3D-Print Pipeline**:
   - Use DALL-E to generate a concept image
   - Convert to 3D with TRELLIS or ShapE
   - Modify with natural language using GPT Modifier
   - Prepare for printing with Wireframer
   - Export G-code for fabrication

2. **Image-to-Wireframe Fabrication**:
   - Import a reference image to TRELLIS
   - Generate a 3D model
   - Transform into a printable wireframe structure
   - Export G-code for robotic arm printing

## Contributing

Contributions to CoFAB are welcome! Please feel free to submit a pull request or open an issue to report bugs or request features.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is proprietary research software, developed as part of an academic thesis. For licensing inquiries, please contact the author.

## Acknowledgments

- This project leverages several AI models including OpenAI's DALL-E, GPT-4, and ShapE
- Special thanks to the advisors and collaborators who supported this research

## Contact

Junke Zhao - zhao.junke0626@gmail.com / junkez@andrew.cmu.edu

Project Link: https://www.junkezhao.design
