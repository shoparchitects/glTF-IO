using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using glTFLoader.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace glTF_BinExporter
{
    public class ObjectExportData
    {
        public Rhino.Geometry.Mesh[] Meshes = null;
        public Rhino.Geometry.Transform Transform = Rhino.Geometry.Transform.Identity;
        public Rhino.Render.RenderMaterial RenderMaterial = null;
        public Rhino.DocObjects.RhinoObject Object = null;
        //thl@SHoP
        public Transform Reflection = Transform.Identity;
    }

    class RhinoDocGltfConverter
    {
        public RhinoDocGltfConverter(glTFExportOptions options, bool binary, RhinoDoc doc, IEnumerable<Rhino.DocObjects.RhinoObject> objects, Rhino.Render.LinearWorkflow workflow)
        {
            this.doc = doc;
            this.options = options;
            this.binary = binary;
            this.objects = objects;
            this.workflow = workflow;
        }

        public RhinoDocGltfConverter(glTFExportOptions options, bool binary, RhinoDoc doc, Rhino.Render.LinearWorkflow workflow)
        {
            this.doc = doc;
            this.options = options;
            this.binary = binary;
            this.objects = doc.Objects;
            this.workflow = null;
        }

        private RhinoDoc doc = null;

        private IEnumerable<Rhino.DocObjects.RhinoObject> objects = null;

        private bool binary = false;
        private glTFExportOptions options = null;
        private Rhino.Render.LinearWorkflow workflow = null;

        private Dictionary<Guid, int> materialsMap = new Dictionary<Guid, int>();

        private gltfSchemaDummy dummy = new gltfSchemaDummy();

        private List<byte> binaryBuffer = new List<byte>();

        private Dictionary<int, glTFLoader.Schema.Node> layers = new Dictionary<int, glTFLoader.Schema.Node>();

        private Rhino.Render.RenderMaterial defaultMaterial = null;
        private Rhino.Render.RenderMaterial DefaultMaterial
        {
            get
            {
                if (defaultMaterial == null)
                {
                    defaultMaterial = Rhino.DocObjects.Material.DefaultMaterial.RenderMaterial;
                }

                return defaultMaterial;
            }
        }
        public glTFLoader.Schema.Gltf ConvertToGltf()
        {
            dummy.Scene = 0;
            dummy.Scenes.Add(new gltfSchemaSceneDummy());

            dummy.Asset = new glTFLoader.Schema.Asset()
            {
                Version = "2.0",
            };

            dummy.Samplers.Add(new glTFLoader.Schema.Sampler()
            {
                MinFilter = glTFLoader.Schema.Sampler.MinFilterEnum.LINEAR,
                MagFilter = glTFLoader.Schema.Sampler.MagFilterEnum.LINEAR,
                WrapS = glTFLoader.Schema.Sampler.WrapSEnum.REPEAT,
                WrapT = glTFLoader.Schema.Sampler.WrapTEnum.REPEAT,
            });

            if (options.UseDracoCompression)
            {
                dummy.ExtensionsUsed.Add(glTFExtensions.KHR_draco_mesh_compression.Tag);
                dummy.ExtensionsRequired.Add(glTFExtensions.KHR_draco_mesh_compression.Tag);
            }

            dummy.ExtensionsUsed.Add(glTFExtensions.KHR_materials_transmission.Tag);
            dummy.ExtensionsUsed.Add(glTFExtensions.KHR_materials_clearcoat.Tag);
            dummy.ExtensionsUsed.Add(glTFExtensions.KHR_materials_ior.Tag);
            dummy.ExtensionsUsed.Add(glTFExtensions.KHR_materials_specular.Tag);


            IEnumerable<Rhino.DocObjects.RhinoObject> pointClouds = objects.Where(x => x.ObjectType == Rhino.DocObjects.ObjectType.PointSet);

            foreach (Rhino.DocObjects.RhinoObject rhinoObject in pointClouds)
            {
                RhinoPointCloudGltfConverter converter = new RhinoPointCloudGltfConverter(rhinoObject, options, binary, dummy, binaryBuffer);
                int meshIndex = converter.AddPointCloud();

                if (meshIndex != -1)
                {
                    glTFLoader.Schema.Node node = new glTFLoader.Schema.Node()
                    {
                        Mesh = meshIndex,
                        Name = GetObjectName(rhinoObject),
                    };

                    int nodeIndex = dummy.Nodes.AddAndReturnIndex(node);

                    AddNode(nodeIndex, rhinoObject);
                }
            }

            var sanitized = SanitizeRhinoObjects(objects);

            // thl @ SHoP - adding this to keep track of repeating meshes
            Dictionary<Guid, int> geo2meshIndex = new Dictionary<Guid, int>();

            foreach (ObjectExportData exportData in sanitized)
            {
                int? materialIndex = GetMaterial(exportData.RenderMaterial, exportData.Object);

                var ogGeometry = exportData.Object;

                int meshIndex;
                RhinoMeshGltfConverter meshConverter;

                //thl@SHoP
                //this is where the magic happens - looking up previous meshes to see if we can instance/link to an existing mesh so we don't create a repetative mesh
                bool needsLookupMirroredMeshID = options.FlipMirroredNormals && exportData.Reflection != Transform.Identity;

                if (geo2meshIndex.ContainsKey(ogGeometry.Id))
                {

                    if (needsLookupMirroredMeshID)
                    {
                        if (!OriginalRhinoID2MirroredMeshID.ContainsKey(ogGeometry.Id))//if it does not already exists then we have to create it
                        {
                            meshConverter = new RhinoMeshGltfConverter(exportData, materialIndex, options, binary, dummy, binaryBuffer);
                            meshIndex = meshConverter.AddMesh();

                            OriginalRhinoID2MirroredMeshID.Add(ogGeometry.Id, meshIndex);

                        }
                        meshIndex = OriginalRhinoID2MirroredMeshID[ogGeometry.Id];
                    }
                    else
                    {
                        meshIndex = geo2meshIndex[ogGeometry.Id];
                    }
                }
                else
                {
                    //This is to take care of scenarios where flipped geometry is encountered before encountering original geometry
                    if (needsLookupMirroredMeshID) //making sure we record the original geometry in the meshes first
                    {
                        var tempInverse = exportData.Reflection;
                        exportData.Reflection = Transform.Identity;//first make it not mirrored

                        //ADDING ORIGINAL
                        meshConverter = new RhinoMeshGltfConverter(exportData, materialIndex, options, binary, dummy, binaryBuffer);
                        meshIndex = meshConverter.AddMesh();
                        geo2meshIndex.Add(ogGeometry.Id, meshIndex);

                        exportData.Reflection = tempInverse;//flip it back to mirrored afterwards

                        //ADDING MIRRORED
                        //Now adding the mirrored Meshes
                        meshConverter = new RhinoMeshGltfConverter(exportData, materialIndex, options, binary, dummy, binaryBuffer);
                        meshIndex = meshConverter.AddMesh();
                        OriginalRhinoID2MirroredMeshID.Add(ogGeometry.Id, meshIndex);

                    }
                    else
                    {
                        //ONLY ADDING MIRRORED
                        meshConverter = new RhinoMeshGltfConverter(exportData, materialIndex, options, binary, dummy, binaryBuffer);
                        meshIndex = meshConverter.AddMesh();
                        geo2meshIndex.Add(ogGeometry.Id, meshIndex);
                    }
                }

                glTFLoader.Schema.Node node = new glTFLoader.Schema.Node()
                {
                    Mesh = meshIndex,
                    Name = GetObjectName(exportData.Object),
                };

                int nodeIndex = dummy.Nodes.AddAndReturnIndex(node);

                AddNode(nodeIndex, exportData.Object);

                //thl @ SHoP - We have to link the mesh node back to the block node
                if (ExportData2BlockInstanceNodeIndex.ContainsKey(exportData))
                {
                    var blockNodeIndex = ExportData2BlockInstanceNodeIndex[exportData];
                    var blockNode = dummy.Nodes[blockNodeIndex];

                    if (blockNode.Children == null)
                    {
                        blockNode.Children = new int[1] { nodeIndex };
                    }
                    else
                    {
                        blockNode.Children = blockNode.Children.Append(nodeIndex).ToArray();
                    }
                }
            }

            if (binary && binaryBuffer.Count > 0)
            {
                //have to add the empty buffer for the binary file header
                dummy.Buffers.Add(new glTFLoader.Schema.Buffer()
                {
                    ByteLength = (int)binaryBuffer.Count,
                    Uri = null,
                });
            }

            return dummy.ToSchemaGltf();
        }

        private void AddNode(int nodeIndex, Rhino.DocObjects.RhinoObject rhinoObject)
        {
            //thl @ SHoP - Adding additional check so we don't add embedded objects to root level
            if (EmbeddedObjects.Contains(rhinoObject))
                return;
            if (options.ExportLayers)
            {
                AddToLayer(doc.Layers[rhinoObject.Attributes.LayerIndex], nodeIndex);
            }
            else
            {
                dummy.Scenes[dummy.Scene].Nodes.Add(nodeIndex);
            }
        }

        //thl @ SHoP
        private void AddBlockNodeToScene(int nodeIndex, Rhino.DocObjects.RhinoObject rhinoObject)
        {
            if (options.ExportLayers)
            {
                AddToLayer(doc.Layers[rhinoObject.Attributes.LayerIndex], nodeIndex);
            }
            else
            {
                dummy.Scenes[dummy.Scene].Nodes.Add(nodeIndex);
            }
        }

        //thl @ SHoP - modified to only be called at root level. We are not tracking layers embedded in blocks
        private void AddToLayer(Rhino.DocObjects.Layer layer, int child)
        {
            if (layers.TryGetValue(layer.Index, out glTFLoader.Schema.Node node))
            {
                if (node.Children == null)
                {
                    node.Children = new int[1] { child };
                }
                else
                {
                    node.Children = node.Children.Append(child).ToArray();
                }
            }
            else
            {
                node = new glTFLoader.Schema.Node()
                {
                    Name = layer.Name,
                    Children = new int[1] { child },
                };

                layers.Add(layer.Index, node);

                int nodeIndex = dummy.Nodes.AddAndReturnIndex(node);

                Rhino.DocObjects.Layer parentLayer = doc.Layers.FindId(layer.ParentLayerId);

                if (parentLayer == null)
                {
                    dummy.Scenes[dummy.Scene].Nodes.Add(nodeIndex);
                }
                else
                {
                    AddToLayer(parentLayer, nodeIndex);
                }
            }
        }

        public string GetObjectName(Rhino.DocObjects.RhinoObject rhinoObject)
        {
            return string.IsNullOrEmpty(rhinoObject.Name) ? null : rhinoObject.Name;
        }

        public byte[] GetBinaryBuffer()
        {
            return binaryBuffer.ToArray();
        }

        int? GetMaterial(Rhino.Render.RenderMaterial material, Rhino.DocObjects.RhinoObject rhinoObject)
        {
            if (!options.ExportMaterials)
            {
                return null;
            }

            if (material == null && options.UseDisplayColorForUnsetMaterials)
            {
                Rhino.Display.Color4f objectColor = GetObjectColor(rhinoObject);
                //thl@SHoP start
                if (_DisplayColorToMaterialIndex.ContainsKey(objectColor))
                {
                    return _DisplayColorToMaterialIndex[objectColor];
                }
                else
                {
                    var matIndex =  CreateSolidColorMaterial(objectColor);

                    _DisplayColorToMaterialIndex.Add(objectColor, matIndex);

                    return matIndex;

                }
                //thl@SHoP end

            }
            else if (material == null)
            {
                material = DefaultMaterial;
            }

            Guid materialId = material.Id;

            if (!materialsMap.TryGetValue(materialId, out int materialIndex))
            {
                RhinoMaterialGltfConverter materialConverter = new RhinoMaterialGltfConverter(options, binary, dummy, binaryBuffer, material, workflow);
                materialIndex = materialConverter.AddMaterial();
                materialsMap.Add(materialId, materialIndex);
            }

            return materialIndex;
        }

        int CreateSolidColorMaterial(Rhino.Display.Color4f color)
        {
            glTFLoader.Schema.Material material = new glTFLoader.Schema.Material()
            {
                PbrMetallicRoughness = new glTFLoader.Schema.MaterialPbrMetallicRoughness()
                {
                    BaseColorFactor = color.ToFloatArray(),
                }
            };

            return dummy.Materials.AddAndReturnIndex(material);
        }

        Rhino.Display.Color4f GetObjectColor(Rhino.DocObjects.RhinoObject rhinoObject)
        {
            if (rhinoObject.Attributes.ColorSource == Rhino.DocObjects.ObjectColorSource.ColorFromLayer)
            {
                int layerIndex = rhinoObject.Attributes.LayerIndex;

                return new Rhino.Display.Color4f(rhinoObject.Document.Layers[layerIndex].Color);
            }
            else
            {
                return new Rhino.Display.Color4f(rhinoObject.Attributes.ObjectColor);
            }
        }

        public Rhino.Geometry.Mesh[] GetMeshes(Rhino.DocObjects.RhinoObject rhinoObject)
        {

            if (rhinoObject.ObjectType == Rhino.DocObjects.ObjectType.Mesh)
            {
                Rhino.DocObjects.MeshObject meshObj = rhinoObject as Rhino.DocObjects.MeshObject;

                return new Rhino.Geometry.Mesh[] { meshObj.MeshGeometry };
            }
            else if (rhinoObject.ObjectType == Rhino.DocObjects.ObjectType.SubD)
            {
                Rhino.DocObjects.SubDObject subdObject = rhinoObject as Rhino.DocObjects.SubDObject;

                Rhino.Geometry.SubD subd = subdObject.Geometry as Rhino.Geometry.SubD;

                Rhino.Geometry.Mesh mesh = null;

                if (options.SubDExportMode == SubDMode.ControlNet)
                {
                    mesh = Rhino.Geometry.Mesh.CreateFromSubDControlNet(subd);
                }
                else
                {
                    int level = options.SubDLevel;

                    mesh = Rhino.Geometry.Mesh.CreateFromSubD(subd, level);
                }

                return new Rhino.Geometry.Mesh[] { mesh };
            }

            // Need to get a Mesh from the None-mesh object. Using the FastRenderMesh here. Could be made configurable.
            // First make sure the internal rhino mesh has been created
            rhinoObject.CreateMeshes(Rhino.Geometry.MeshType.Preview, Rhino.Geometry.MeshingParameters.FastRenderMesh, true);

            // Then get the internal rhino meshes
            Rhino.Geometry.Mesh[] meshes = rhinoObject.GetMeshes(Rhino.Geometry.MeshType.Preview);

            List<Rhino.Geometry.Mesh> validMeshes = new List<Rhino.Geometry.Mesh>();

            foreach (Rhino.Geometry.Mesh mesh in meshes)
            {
                if (MeshIsValidForExport(mesh))
                {
                    mesh.EnsurePrivateCopy();
                    validMeshes.Add(mesh);
                }
            }

            return validMeshes.ToArray();
        }

        public bool MeshIsValidForExport(Rhino.Geometry.Mesh mesh)
        {
            if (mesh == null)
            {
                return false;
            }

            if (mesh.Vertices.Count == 0)
            {
                return false;
            }

            if (mesh.Faces.Count == 0)
            {
                return false;
            }

            if (!options.ExportOpenMeshes && !mesh.IsClosed)
            {
                return false;
            }

            return true;
        }

        private string GetDebugName(Rhino.DocObjects.RhinoObject rhinoObject)
        {
            if (string.IsNullOrEmpty(rhinoObject.Name))
            {
                return "(Unnamed)";
            }

            return rhinoObject.Name;
        }

        /// <summary>
        /// This is to address blocks
        /// </summary>
        /// <param name="rhinoObjects"></param>
        /// <returns></returns>
        public List<ObjectExportData> SanitizeRhinoObjects(IEnumerable<Rhino.DocObjects.RhinoObject> rhinoObjects)
        {
            List<ObjectExportData> processedObjects = new List<ObjectExportData>();

            foreach (var rhinoObject in rhinoObjects)
            {
                var nodeIndex = createBlockNodesRecursive(rhinoObject,null,Transform.Identity,processedObjects);
                
            }

            //Remove Unmeshable
            processedObjects.RemoveAll(x => !x.Object.IsMeshable(Rhino.Geometry.MeshType.Any));

            foreach (var item in processedObjects)
            {
                //Mesh

                if (item.Object.ObjectType == Rhino.DocObjects.ObjectType.SubD && item.Object.Geometry is Rhino.Geometry.SubD subd)
                {
                    if (options.SubDExportMode == SubDMode.ControlNet)
                    {
                        Rhino.Geometry.Mesh mesh = Rhino.Geometry.Mesh.CreateFromSubDControlNet(subd);

                        mesh.Transform(item.Transform);

                        item.Meshes = new Rhino.Geometry.Mesh[] { mesh };
                    }
                    else
                    {
                        int level = options.SubDLevel;

                        Rhino.Geometry.Mesh mesh = Rhino.Geometry.Mesh.CreateFromSubD(subd, level);

                        mesh.Transform(item.Transform);

                        item.Meshes = new Rhino.Geometry.Mesh[] { mesh };
                    }
                }
                else
                {
                    Rhino.Geometry.MeshingParameters parameters = item.Object.GetRenderMeshParameters();

                    if (item.Object.MeshCount(Rhino.Geometry.MeshType.Render, parameters) == 0)
                    {
                        item.Object.CreateMeshes(Rhino.Geometry.MeshType.Render, parameters, false);
                    }

                    List <Rhino.Geometry.Mesh> meshes = new List<Rhino.Geometry.Mesh>(item.Object.GetMeshes(Rhino.Geometry.MeshType.Render));


                    foreach (Rhino.Geometry.Mesh mesh in meshes)
                    {
                        mesh.EnsurePrivateCopy();
                        mesh.Transform(item.Transform);
                    }

                    //Remove bad meshes
                    meshes.RemoveAll(x => x == null || !MeshIsValidForExport(x));

                    //start thl @ SHoP

                    Rhino.Geometry.Mesh joinedRenderMesh = new Rhino.Geometry.Mesh();

                    if (meshes.Count > 0)
                    {

                        joinedRenderMesh.Append(meshes);

                        item.Meshes = new Rhino.Geometry.Mesh[1] { joinedRenderMesh };
                    }
                    //end thl @ shop

                }
            }

            //Remove meshless objects
            processedObjects.RemoveAll(x => x.Meshes.Length == 0);

            return processedObjects;
        }

        private Rhino.Render.RenderMaterial GetObjectMaterial(Rhino.DocObjects.RhinoObject rhinoObject)
        {
            Rhino.DocObjects.ObjectMaterialSource source = rhinoObject.Attributes.MaterialSource;

            Rhino.Render.RenderMaterial renderMaterial = null;

            if (source == Rhino.DocObjects.ObjectMaterialSource.MaterialFromObject)
            {
                renderMaterial = rhinoObject.RenderMaterial;
            }
            else if (source == Rhino.DocObjects.ObjectMaterialSource.MaterialFromLayer)
            {
                int layerIndex = rhinoObject.Attributes.LayerIndex;

                renderMaterial = GetLayerMaterial(layerIndex);
            }

            return renderMaterial;
        }

        private Rhino.Render.RenderMaterial GetLayerMaterial(int layerIndex)
        {
            if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            {
                return null;
            }

            return doc.Layers[layerIndex].RenderMaterial;
        }

        #region SHoP Custom
        //thl @ SHoP
        //the return int list is the indices of the immediate children nested block definition node
        private int createBlockNodesRecursive(RhinoObject rhinoObject, RhinoObject parent, Transform parentReflection, List<ObjectExportData> processedObjects)
        {
            if(parent != null)
                EmbeddedObjects.Add(rhinoObject);


            if (rhinoObject is Rhino.DocObjects.InstanceObject instanceObject)//if a block
            {
                var blockDefinition = instanceObject.InstanceDefinition;
                var instanceName = getBlockInstanceName(instanceObject);
                ExtrasSHoP extras = new ExtrasSHoP
                {
                    instanceOf = blockDefinition.Name,
                    instanceId = BlockDefToCount[blockDefinition]
                };

                if(instanceObject.Attributes.UserStringCount > 0)
                {
                    
                    StringBuilder stringBuilder = new StringBuilder();
                    stringBuilder.Append("{");

                    var collection = instanceObject.Attributes.GetUserStrings();
                    for (int i = 0; i < collection.Count; i++)
                    {
                        var tag = collection.GetKey(i);
                        var value = collection.Get(i);
                        string[] values = value.Split(',');
                        stringBuilder.Append($"\"{tag}\" :");
                        stringBuilder.Append($"{JsonConvert.SerializeObject(values)},");
                    }

                    stringBuilder.Append("}");
                    extras.tags = JObject.Parse(stringBuilder.ToString());
                }
                Node nodeBlockInstance = null;
                Transform scaledWunitsTransform = instanceObject.InstanceXform;

                if (parent == null)//TODO need to scale based on unit conversion the translation part of the transform only at the root level
                {
                    scaledWunitsTransform.M03 *= options.ScaleFactor;
                    scaledWunitsTransform.M13 *= options.ScaleFactor;
                    scaledWunitsTransform.M23 *= options.ScaleFactor;
                }

                nodeBlockInstance = createBlockNode(instanceName,
                                                    scaledWunitsTransform,
                                                    parentReflection,
                                                    out Transform reflection,
                                                    -1,
                                                    extras);

                var nodeIndex_BlockInstance = dummy.Nodes.AddAndReturnIndex(nodeBlockInstance);
                if(parent == null)
                    AddBlockNodeToScene(nodeIndex_BlockInstance, rhinoObject);

                BlockInstance2NodeIndex.Add(instanceObject, nodeIndex_BlockInstance);

                List<int> children = new List<int>();
                for (int i = 0; i < instanceObject.InstanceDefinition.ObjectCount; i++)
                {
                    Rhino.DocObjects.RhinoObject objectInsideBlock = instanceObject.InstanceDefinition.Object(i);

                    var nodeIndex = createBlockNodesRecursive(objectInsideBlock, instanceObject, parentReflection * reflection , processedObjects); //using logical XOR for mirrored and parent mirrored because if both are true then its no longer mirroed

                    if(nodeIndex >= 0)
                        children.Add(nodeIndex);
                }

                if (children.Count > 0)
                    nodeBlockInstance.Children = children.ToArray();

                return nodeIndex_BlockInstance;
            }
            else//if just geometry
            {
                var geoExportData = new ObjectExportData()
                {
                    Object = rhinoObject,
                    //Transform = Transform.Identity,
                    RenderMaterial = GetObjectMaterial(rhinoObject),
                    Reflection = parentReflection
                };

                if(parent != null)                //we also need to add to the dictionary to track to 
                    ExportData2BlockInstanceNodeIndex.Add(geoExportData, BlockInstance2NodeIndex[parent]);

                processedObjects.Add(geoExportData);

                return -1; // when hitting a leaf
            }
            
        }

        string getBlockInstanceName(Rhino.DocObjects.InstanceObject instanceObj)
        {
            var name = GetObjectName(instanceObj);

            if (!BlockDefToCount.ContainsKey(instanceObj.InstanceDefinition))
                BlockDefToCount.Add(instanceObj.InstanceDefinition, -1);

            BlockDefToCount[instanceObj.InstanceDefinition]++;

            if (string.IsNullOrWhiteSpace(name))
            {
                return $"{instanceObj.InstanceDefinition.Name}.{BlockDefToCount[instanceObj.InstanceDefinition]}";
            }
            return name;
        }

        bool isMirrored(Transform trans, Vector3d diag)
        {
            bool mirrored = false;

            var simType = trans.SimilarityType;//mirror or not -1: orientationReversing, 0: Notsimilarity, 1: orientationPreserviing
                                               //NotSimilarity takes precedence over orientationReversing, meaning if you mirror and "deform"(NU scale) then it shows NotSimilarity

            //need to handle if it's mirrored
            if (simType == TransformSimilarityType.OrientationReversing)
            {
                mirrored = true;
            }
            else if (simType == TransformSimilarityType.NotSimilarity)
            {
                if (diag.X < 0 || diag.Y < 0 || diag.Z < 0)//now check if there's any negative sign in the diagonal to see any mirroring
                {
                    mirrored = true;
                }
            }

            return mirrored;
        }

        Node createBlockNode(string name, Transform trans, Transform parentReflection, out Transform reflection, int child = -1, ExtrasSHoP extras = null)
        {
            //CHECKING SELF START
            reflection = Transform.Identity;
            //var mirrored = false; // orientation preserved = ! mirrored
            Vector3d translationWparent, diag;
            Transform rotation, orth;
            Quaternion quaternion = Quaternion.Identity;

            trans.DecomposeAffine(out translationWparent, out rotation, out orth, out diag);
            /*var rotationMatrix = Transform2Matrix(rotation);
            quaternion = Matrix2Quaternion(rotationMatrix);*/



            /*var rigidType = trans.RigidType;//scaling or not (not sure when orientation reverseing happens)

            *//*if (rigidType == TransformRigidType.Rigid)
                diag = new Vector3d(1, 1, 1);*/

            var mirrored = isMirrored(trans, diag);

            if(mirrored)
            {
                //Let's solve Rotation * Reflection * Scaling = Transformation
                // Reflection * Scaling = Transformation * Inverse Rotation
                // Reflection = Transformation * Inverse Rotation * Inverse Scaling
                //rotation then does not account for reflection
                var transformation = trans.Clone();
                transformation.Linearize(); //getting rid of translation, leaving only the rotation(including reflection)

                rotation.TryGetInverse(out var inverseRotation);//Getting the inverse of the rotation(including reflection)
                var scaleMatrix = Transform.Diagonal(-diag); //flipping the sign because scaling we don't want to take out the reflection thru scaling
                scaleMatrix.TryGetInverse(out var inverseScaling);
                reflection = transformation * inverseRotation * inverseScaling;//Trying to get the matrix which rotation can multiply to get to the rotation with reflection
            }
            //CHECKING SELF END

            //CHECKING PARENT * SELF START
            trans = parentReflection * trans;//Matrix multiplication is not communitive so can't reverse the order!!
            trans.DecomposeAffine(out translationWparent, out rotation, out orth, out var diagWparent);
            var rotationMatrix = Transform2Matrix(rotation);
            quaternion = Matrix2Quaternion(rotationMatrix);

            if (options.MapRhinoZToGltfY)
            {
                translationWparent.Transform(Constants.ZtoYUp);
                quaternion = new Quaternion(quaternion.A, quaternion.B, quaternion.D, -quaternion.C);//half empirical half
                                                                                                     //stackoverflow post - https://stackoverflow.com/questions/16099979/can-i-switch-x-y-z-in-a-quaternion
                diag = new Vector3d(diag.X, diag.Z, diag.Y);
            }

            //mirrored = isMirrored(trans, diagWparent);
            //CHECKING PARENT * SELF END


            if (options.FlipMirroredNormals && mirrored)
                diag *= -1;

            Node node = new glTFLoader.Schema.Node()
            {
                Name = name,
                Translation = new float[3] { (float)translationWparent.X, (float)translationWparent.Y, (float)translationWparent.Z },
                Rotation = new float[4] { (float)quaternion.B, (float)quaternion.C, (float)quaternion.D, (float)quaternion.A },
                Scale = new float[3] { (float)diag.X, (float)diag.Y, (float)diag.Z}
            };
            if(child >= 0)
                node.Children = new int[] { child };

            if (extras != null)
            {
                node.Extras = extras;
            }
            return node;
        }

        public static Quaternion ToQuaternion(double yaw, double pitch, double roll) // yaw (Z), pitch (Y), roll (X)
        {
            // Abbreviations for the various angular functions
            double cy = Math.Cos(yaw * 0.5);//z(w)
            double sy = Math.Sin(yaw * 0.5);//z(w)
            double cp = Math.Cos(pitch * 0.5);//v(y)
            double sp = Math.Sin(pitch * 0.5);//v(y)
            double cr = Math.Cos(roll * 0.5);//u(x)
            double sr = Math.Sin(roll * 0.5);//u(x)

            Quaternion q;
            q = Quaternion.Zero;
            q.A = cy * cp * cr + sy * sp * sr;
            q.B = cy * cp * sr - sy * sp * cr;
            q.C = sy * cp * sr + cy * sp * cr;
            q.D = sy * cp * cr - cy * sp * sr;

            return q;
        }

        /// <summary>
        /// Converting from Rhino Transform Matrix to System Numerics 4x4 Matrix
        /// </summary>
        /// <param name="rotationMatrix"></param>
        /// <returns></returns>
        public static System.Numerics.Matrix4x4 Transform2Matrix(Transform rotationMatrix)
        {
            if (rotationMatrix == null)
                return default;

            rotationMatrix = rotationMatrix.Transpose();
            return new System.Numerics.Matrix4x4(
                                                (float)rotationMatrix.M00,
                                                (float)rotationMatrix.M01,
                                                (float)rotationMatrix.M02,
                                                (float)rotationMatrix.M03,
                                                (float)rotationMatrix.M10,
                                                (float)rotationMatrix.M11,
                                                (float)rotationMatrix.M12,
                                                (float)rotationMatrix.M13,
                                                (float)rotationMatrix.M20,
                                                (float)rotationMatrix.M21,
                                                (float)rotationMatrix.M22,
                                                (float)rotationMatrix.M23,
                                                (float)rotationMatrix.M30,
                                                (float)rotationMatrix.M31,
                                                (float)rotationMatrix.M32,
                                                (float)rotationMatrix.M33);

        }


        /// <summary>
        /// Transform from System Numerics 4x4 Matrix to Rhino Quaternion
        /// </summary>
        /// <param name="matrix"></param>
        /// <returns></returns>
        public static Quaternion Matrix2Quaternion(System.Numerics.Matrix4x4 matrix)
        {
            var quaternionSystem = System.Numerics.Quaternion.CreateFromRotationMatrix(matrix);

            return new Quaternion(quaternionSystem.W,quaternionSystem.X,quaternionSystem.Y,quaternionSystem.Z);
        }

        //Dictionary keeping track of sanitized object to block definition: Sanitized object => Instance Definition
        //Dictionary<ObjectExportData, Rhino.DocObjects.InstanceDefinition> BlockInstanceData2BlockDef = new Dictionary<ObjectExportData, Rhino.DocObjects.InstanceDefinition>();

        //Dictionary keeping track of Block Definition to node index: Instance Definition => Node Index int
        Dictionary<Rhino.DocObjects.RhinoObject, int> BlockInstance2NodeIndex = new Dictionary<Rhino.DocObjects.RhinoObject, int>();

        //Dictionary keeping track of Block Definition to immediate meshes: ExportData => Block Def Node Index
        Dictionary<ObjectExportData, int> ExportData2BlockInstanceNodeIndex = new Dictionary<ObjectExportData, int>();

        //Root Level BlockInstanceNodeIndices
        //List<int> RootBlockInstanceNodeIndices = new List<int>();

        //Block Instance Counter
        Dictionary<Rhino.DocObjects.InstanceDefinition, int> BlockDefToCount = new Dictionary<Rhino.DocObjects.InstanceDefinition, int>();

        //Embedded Meshes (used to distinguish from root level meshes)
        List<Rhino.DocObjects.RhinoObject> EmbeddedObjects = new List<Rhino.DocObjects.RhinoObject>();
        
        //Tracking Display color materials if no material is assigned to layers
        Dictionary<Rhino.Display.Color4f, int> _DisplayColorToMaterialIndex = new Dictionary<Rhino.Display.Color4f, int>();

        //Tracking mirrored geometry ids- Original Rhino Object GUID -> mesh ID
        Dictionary<Guid,int> OriginalRhinoID2MirroredMeshID = new Dictionary<Guid,int>();

        class ExtrasSHoP : Extras
        {
            public string instanceOf;
            public int instanceId;
            public dynamic tags;
        }


        #endregion

        #region ARCHIVE
        /// <summary>
        /// thl@SHoP - ARCHIVE
        /// </summary>
        /// <param name="instanceObject"></param>
        /// <param name="instanceTransform"></param>
        /// <param name="pieces"></param>
        /// <param name="transforms"></param>
        private void ExplodeRecursive(Rhino.DocObjects.InstanceObject instanceObject, Rhino.Geometry.Transform instanceTransform, List<Rhino.DocObjects.RhinoObject> pieces, List<Rhino.Geometry.Transform> transforms)
        {
            for (int i = 0; i < instanceObject.InstanceDefinition.ObjectCount; i++)
            {
                Rhino.DocObjects.RhinoObject rhinoObject = instanceObject.InstanceDefinition.Object(i);

                if (rhinoObject is Rhino.DocObjects.InstanceObject nestedObject)
                {
                    Rhino.Geometry.Transform nestedTransform = instanceTransform * nestedObject.InstanceXform;

                    ExplodeRecursive(nestedObject, nestedTransform, pieces, transforms);
                }
                else
                {
                    pieces.Add(rhinoObject);

                    transforms.Add(instanceTransform);
                }
            }
        }
        #endregion
    }
}
