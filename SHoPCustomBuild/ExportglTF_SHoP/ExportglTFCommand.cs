using glTF_BinExporter;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using System;
using System.Collections.Generic;

namespace ExportglTF_SHoP
{


    public class ExportglTFCommand : Command
    {
        public ExportglTFCommand()
        {
            Instance = this;
        }

        ///<summary>The only instance of this command.</summary>
        public static ExportglTFCommand Instance { get; private set; }

        ///<returns>The command name as it appears on the Rhino command line.</returns>
        public override string EnglishName => "ExportglTF_SHoP";

        // Persisted option values
        private bool _useDraco = false;
        private bool _mapZtoY = true;
        private bool _exportMaterials = true;
        private bool _exportLayers = true;
        private bool _flipMirroredNormals = true;

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // 1) Select objects + set options
            var go = new GetObject();
            go.AlreadySelectedObjectSelect = true;

            go.SetCommandPrompt("Select objects to export");
            var optDraco = new OptionToggle(_useDraco, "No", "Yes");
            var optZtoY = new OptionToggle(_mapZtoY, "No", "Yes");
            var optMats = new OptionToggle(_exportMaterials, "No", "Yes");
            var optLayers = new OptionToggle(_exportLayers, "No", "Yes");
            var optFlip = new OptionToggle(_flipMirroredNormals, "No", "Yes");

            go.AddOptionToggle("UseDraco", ref optDraco);
            go.AddOptionToggle("MapRhinoZtoGltfY", ref optZtoY);
            go.AddOptionToggle("ExportMaterials", ref optMats);
            go.AddOptionToggle("ExportLayers", ref optLayers);
            go.AddOptionToggle("FlipMirroredNormals", ref optFlip);

            while (true)
            {
                var res = go.GetMultiple(1, 0);
                if (res == GetResult.Option) continue;
                if (res != GetResult.Object) return Result.Cancel;
                break;
            }

            _useDraco = optDraco.CurrentValue;
            _mapZtoY = optZtoY.CurrentValue;
            _exportMaterials = optMats.CurrentValue;
            _exportLayers = optLayers.CurrentValue;
            _flipMirroredNormals = optFlip.CurrentValue;

            var rhObjects = new List<RhinoObject>();
            for (int i = 0; i < go.ObjectCount; i++)
                rhObjects.Add(go.Object(i).Object());

            // 2) Output path
            var gs = new GetString();
            gs.SetCommandPrompt("Output .glb/.gltf path");
            gs.SetDefaultString(System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "temp.glb"));
            gs.AcceptNothing(true);
            var sres = gs.Get();
            if (sres != GetResult.String && sres != GetResult.Nothing)
                return Result.Cancel;
            string path = gs.StringResult();

            // 3) Export
            glTFExportOptions exportOptions = glTFBinExporterPlugin.GetSavedOptions();
            exportOptions.UseDracoCompression = _useDraco;
            exportOptions.MapRhinoZToGltfY = _mapZtoY;
            exportOptions.ExportMaterials = _exportMaterials;
            exportOptions.ExportLayers = _exportLayers;
            exportOptions.FlipMirroredNormals = _flipMirroredNormals;

            bool binary = System.IO.Path.GetExtension(path).ToLower() == ".glb";
            bool ok = glTFBinExporterPlugin.DoExport(path, exportOptions, binary, doc, rhObjects, doc.RenderSettings.LinearWorkflow);
            return ok ? Result.Success : Result.Failure;
        }

    }

}
