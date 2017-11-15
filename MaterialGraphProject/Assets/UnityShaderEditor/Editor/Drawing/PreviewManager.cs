﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor.Graphing.Util;
using UnityEngine;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UnityEditor.ShaderGraph.Drawing
{
    public class PreviewManager : IDisposable
    {
        AbstractMaterialGraph m_Graph;
        Dictionary<Guid, PreviewData> m_Previews = new Dictionary<Guid, PreviewData>();
        HashSet<Guid> m_DirtyPreviews = new HashSet<Guid>();
        HashSet<Guid> m_DirtyShaders = new HashSet<Guid>();
        HashSet<Guid> m_TimeDependentPreviews = new HashSet<Guid>();
        Material m_PreviewMaterial;
        MaterialPropertyBlock m_PreviewPropertyBlock;
        PreviewSceneResources m_SceneResources;
        Texture2D m_ErrorTexture;
        DateTime m_LastUpdate;

        public PreviewRate previewRate { get; set; }

        public PreviewManager(AbstractMaterialGraph graph)
        {
            m_Graph = graph;
            m_PreviewMaterial = new Material(Shader.Find("Unlit/Color")) { hideFlags = HideFlags.HideInHierarchy };
            m_PreviewMaterial.hideFlags = HideFlags.HideAndDontSave;
            m_PreviewPropertyBlock = new MaterialPropertyBlock();
            m_ErrorTexture = new Texture2D(2, 2);
            m_ErrorTexture.SetPixel(0, 0, Color.magenta);
            m_ErrorTexture.SetPixel(0, 1, Color.black);
            m_ErrorTexture.SetPixel(1, 0, Color.black);
            m_ErrorTexture.SetPixel(1, 1, Color.magenta);
            m_ErrorTexture.filterMode = FilterMode.Point;
            m_ErrorTexture.Apply();
            m_SceneResources = new PreviewSceneResources();

            foreach (var node in m_Graph.GetNodes<INode>())
                AddPreview(node);
        }

        public PreviewData GetPreview(INode node)
        {
            return m_Previews[node.guid];
        }

        void AddPreview(INode node)
        {
            var previewData = new PreviewData
            {
                node = node,
                renderTexture = new RenderTexture(256, 256, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { hideFlags = HideFlags.HideAndDontSave }
            };
            if (m_Previews.ContainsKey(node.guid))
            {
                Debug.LogWarningFormat("A preview already exists for {0} {1}", node.name, node.guid);
                RemovePreview(node);
            }
            m_Previews.Add(node.guid, previewData);
            m_DirtyShaders.Add(node.guid);
            node.onModified += OnNodeModified;
            if (node.RequiresTime())
                m_TimeDependentPreviews.Add(node.guid);
        }

        void RemovePreview(INode node)
        {
            node.onModified -= OnNodeModified;
            m_Previews.Remove(node.guid);
            m_TimeDependentPreviews.Remove(node.guid);
            m_DirtyPreviews.Remove(node.guid);
            m_DirtyShaders.Remove(node.guid);
        }

        void OnNodeModified(INode node, ModificationScope scope)
        {
            if (scope >= ModificationScope.Graph)
                m_DirtyShaders.Add(node.guid);
            else if (scope == ModificationScope.Node)
                m_DirtyPreviews.Add(node.guid);

            if (node.RequiresTime())
                m_TimeDependentPreviews.Add(node.guid);
            else
                m_TimeDependentPreviews.Remove(node.guid);
        }

        Stack<Guid> m_Wavefront = new Stack<Guid>();

        void PropagateNodeSet(HashSet<Guid> nodeGuidSet, bool forward = true, IEnumerable<Guid> initialWavefront = null)
        {
            m_Wavefront.Clear();
            foreach (var guid in initialWavefront ?? nodeGuidSet)
                m_Wavefront.Push(guid);
            while (m_Wavefront.Count > 0)
            {
                var nodeGuid = m_Wavefront.Pop();
                var node = m_Graph.GetNodeFromGuid(nodeGuid);
                if (node == null)
                    continue;

                // Loop through all nodes that the node feeds into.
                foreach (var slot in forward ? node.GetOutputSlots<ISlot>() : node.GetInputSlots<ISlot>())
                {
                    foreach (var edge in m_Graph.GetEdges(slot.slotReference))
                    {
                        // We look at each node we feed into.
                        var connectedSlot = forward ? edge.inputSlot : edge.outputSlot;
                        var connectedNodeGuid = connectedSlot.nodeGuid;

                        // If the input node is already in the set of time-dependent nodes, we don't need to process it.
                        if (nodeGuidSet.Contains(connectedNodeGuid))
                            continue;

                        // Add the node to the set of time-dependent nodes, and to the wavefront such that we can process the nodes that it feeds into.
                        nodeGuidSet.Add(connectedNodeGuid);
                        m_Wavefront.Push(connectedNodeGuid);
                    }
                }
            }
        }

        HashSet<Guid> m_PropertyNodeGuids = new HashSet<Guid>();
        List<PreviewProperty> m_PreviewProperties = new List<PreviewProperty>();

        public void HandleGraphChanges()
        {
            foreach (var node in m_Graph.removedNodes)
                RemovePreview(node);

            foreach (var node in m_Graph.addedNodes)
                AddPreview(node);

            foreach (var edge in m_Graph.removedEdges)
                m_DirtyShaders.Add(edge.inputSlot.nodeGuid);

            foreach (var edge in m_Graph.addedEdges)
                m_DirtyShaders.Add(edge.inputSlot.nodeGuid);
        }

        List<PreviewData> m_RenderList2D = new List<PreviewData>();
        List<PreviewData> m_RenderList3D = new List<PreviewData>();

        public void RenderPreviews()
        {
            if (previewRate == PreviewRate.Off)
                return;

            var updateTime = DateTime.Now;
            if (previewRate == PreviewRate.Throttled && (updateTime - m_LastUpdate) < TimeSpan.FromSeconds(1.0 / 10.0))
                return;

            m_LastUpdate = updateTime;

            if (m_DirtyShaders.Any())
            {
                PropagateNodeSet(m_DirtyShaders);
                var count = m_DirtyShaders.Count;
                try
                {
                    var i = 0;
                    EditorUtility.DisplayProgressBar("Shader Graph", string.Format("Compiling preview shaders ({0}/{1})", i, count), 0f);
                    foreach (var nodeGuid in m_DirtyShaders)
                    {
                        UpdateShader(nodeGuid);
                        i++;
                        EditorUtility.DisplayProgressBar("Shader Graph", string.Format("Compiling preview shaders ({0}/{1})", i, count), 0f);
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }

                m_DirtyPreviews.UnionWith(m_DirtyShaders);
                m_DirtyShaders.Clear();
            }

            m_DirtyPreviews.UnionWith(m_TimeDependentPreviews);
            PropagateNodeSet(m_DirtyPreviews);

            // Find nodes we need properties from
            m_PropertyNodeGuids.Clear();
            foreach (var nodeGuid in m_DirtyPreviews)
                m_PropertyNodeGuids.Add(nodeGuid);
            PropagateNodeSet(m_PropertyNodeGuids, false);

            // Fill MaterialPropertyBlock
            m_PreviewPropertyBlock.Clear();
            foreach (var nodeGuid in m_PropertyNodeGuids)
            {
                var node = m_Graph.GetNodeFromGuid<AbstractMaterialNode>(nodeGuid);
                if (node == null)
                    continue;
                node.CollectPreviewMaterialProperties(m_PreviewProperties);
                foreach (var prop in m_Graph.properties)
                    m_PreviewProperties.Add(prop.GetPreviewMaterialProperty());

                foreach (var previewProperty in m_PreviewProperties)
                {
                    if (previewProperty.m_PropType == PropertyType.Texture && previewProperty.m_Texture != null)
                        m_PreviewPropertyBlock.SetTexture(previewProperty.m_Name, previewProperty.m_Texture);
                    else if (previewProperty.m_PropType == PropertyType.Cubemap && previewProperty.m_Cubemap != null)
                        m_PreviewPropertyBlock.SetTexture(previewProperty.m_Name, previewProperty.m_Cubemap);
                    else if (previewProperty.m_PropType == PropertyType.Color)
                        m_PreviewPropertyBlock.SetColor(previewProperty.m_Name, previewProperty.m_Color);
                    else if (previewProperty.m_PropType == PropertyType.Vector2)
                        m_PreviewPropertyBlock.SetVector(previewProperty.m_Name, previewProperty.m_Vector4);
                    else if (previewProperty.m_PropType == PropertyType.Vector3)
                        m_PreviewPropertyBlock.SetVector(previewProperty.m_Name, previewProperty.m_Vector4);
                    else if (previewProperty.m_PropType == PropertyType.Vector4)
                        m_PreviewPropertyBlock.SetVector(previewProperty.m_Name, previewProperty.m_Vector4);
                    else if (previewProperty.m_PropType == PropertyType.Float)
                        m_PreviewPropertyBlock.SetFloat(previewProperty.m_Name, previewProperty.m_Float);
                }
                m_PreviewProperties.Clear();
            }

            foreach (var nodeGuid in m_DirtyPreviews)
            {
                PreviewData previewData;
                if (!m_Previews.TryGetValue(nodeGuid, out previewData))
                    continue;
                if (previewData.shader == null)
                {
                    previewData.texture = null;
                    continue;
                }
                if (MaterialGraphAsset.ShaderHasError(previewData.shader))
                {
                    previewData.texture = m_ErrorTexture;
                    continue;
                }
                
                if (previewData.previewMode == PreviewMode.Preview2D)
                    m_RenderList2D.Add(previewData);
                else
                    m_RenderList3D.Add(previewData);
            }

            var time = Time.realtimeSinceStartup;
            EditorUtility.SetCameraAnimateMaterialsTime(m_SceneResources.camera, time);
            m_SceneResources.light0.enabled = true;
            m_SceneResources.light0.intensity = 1.0f;
            m_SceneResources.light0.transform.rotation = Quaternion.Euler(50f, 50f, 0);
            m_SceneResources.light1.enabled = true;
            m_SceneResources.light1.intensity = 1.0f;
            m_SceneResources.camera.clearFlags = CameraClearFlags.Depth;

            // Render 2D previews
            m_SceneResources.camera.transform.position = -Vector3.forward * 2;
            m_SceneResources.camera.transform.rotation = Quaternion.identity;
            m_SceneResources.camera.orthographicSize = 1;
            m_SceneResources.camera.orthographic = true;
            foreach (var previewData in m_RenderList2D)
            {
                m_PreviewMaterial.shader = previewData.shader;
                m_SceneResources.camera.targetTexture = previewData.renderTexture;
                var previousRenderTexure = RenderTexture.active;
                RenderTexture.active = previewData.renderTexture;
                GL.Clear(true, true, Color.black);
                Graphics.Blit(Texture2D.whiteTexture, previewData.renderTexture, m_SceneResources.checkerboardMaterial);
                Graphics.DrawMesh(m_SceneResources.quad, Matrix4x4.identity, m_PreviewMaterial, 1, m_SceneResources.camera, 0, m_PreviewPropertyBlock, ShadowCastingMode.Off, false, null, false);
                var previousUseSRP = Unsupported.useScriptableRenderPipeline;
                Unsupported.useScriptableRenderPipeline = false;
                m_SceneResources.camera.Render();
                Unsupported.useScriptableRenderPipeline = previousUseSRP;
                RenderTexture.active = previousRenderTexure;
                previewData.texture = previewData.renderTexture;
            }
            m_RenderList2D.Clear();

            // Render 3D previews
            m_SceneResources.camera.transform.position = -Vector3.forward * 5;
            m_SceneResources.camera.transform.rotation = Quaternion.identity;
            m_SceneResources.camera.orthographic = false;
            foreach (var previewData in m_RenderList3D)
            {
                m_PreviewMaterial.shader = previewData.shader;
                m_SceneResources.camera.targetTexture = previewData.renderTexture;
                var previousRenderTexure = RenderTexture.active;
                RenderTexture.active = previewData.renderTexture;
                GL.Clear(true, true, Color.black);
                Graphics.Blit(Texture2D.whiteTexture, previewData.renderTexture, m_SceneResources.checkerboardMaterial);
                var mesh = previewData.mesh ?? m_SceneResources.sphere;
                Graphics.DrawMesh(mesh, Matrix4x4.TRS(-mesh.bounds.center, Quaternion.identity, Vector3.one), m_PreviewMaterial, 1, m_SceneResources.camera, 0, m_PreviewPropertyBlock, ShadowCastingMode.Off, false, null, false);
                var previousUseSRP = Unsupported.useScriptableRenderPipeline;
                Unsupported.useScriptableRenderPipeline = previewData.node is IMasterNode;
                m_SceneResources.camera.Render();
                Unsupported.useScriptableRenderPipeline = previousUseSRP;
                RenderTexture.active = previousRenderTexure;
                previewData.texture = previewData.renderTexture;
            }
            m_RenderList3D.Clear();

            m_SceneResources.light0.enabled = false;
            m_SceneResources.light1.enabled = false;

            foreach (var nodeGuid in m_DirtyPreviews)
            {
                PreviewData previewData;
                if (!m_Previews.TryGetValue(nodeGuid, out previewData))
                    continue;

                if (previewData.onPreviewChanged != null)
                    previewData.onPreviewChanged();
            }

            m_DirtyPreviews.Clear();
        }

        void UpdateShader(Guid nodeGuid)
        {
            var node = m_Graph.GetNodeFromGuid<AbstractMaterialNode>(nodeGuid);
            if (node == null)
                return;
            PreviewData previewData;
            if (!m_Previews.TryGetValue(nodeGuid, out previewData))
                return;

             if (!(node is IMasterNode) && (!node.hasPreview || NodeUtils.FindEffectiveShaderStage(node, true) == ShaderStage.Vertex))
            {
                previewData.shaderString = null;
            }
            else
            {
                PreviewMode mode;
                previewData.shaderString = m_Graph.GetPreviewShader(node, out mode);
                previewData.previewMode = mode;
            }

            // Debug output
            Debug.Log("RecreateShader: " + node.GetVariableNameForNode() + Environment.NewLine + previewData.shaderString);
            File.WriteAllText(Application.dataPath + "/../GeneratedShader.shader", (previewData.shaderString ?? "null").Replace("UnityEngine.MaterialGraph", "Generated"));

            if (string.IsNullOrEmpty(previewData.shaderString))
            {
                if (previewData.shader != null)
                    Object.DestroyImmediate(previewData.shader, true);
                previewData.shader = null;
                return;
            }

            if (previewData.shader != null && MaterialGraphAsset.ShaderHasError(previewData.shader))
            {
                ShaderUtil.ClearShaderErrors(previewData.shader);
                Object.DestroyImmediate(previewData.shader, true);
                previewData.shader = null;
            }

            if (previewData.shader == null)
            {
                previewData.shader = ShaderUtil.CreateShaderAsset(previewData.shaderString);
                previewData.shader.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                ShaderUtil.ClearShaderErrors(previewData.shader);
                ShaderUtil.UpdateShaderAsset(previewData.shader, previewData.shaderString);
            }

            if (MaterialGraphAsset.ShaderHasError(previewData.shader))
                Debug.LogWarningFormat("ShaderHasError: {0}\n{1}", node.GetVariableNameForNode(), previewData.shaderString);
        }

        void DestroyPreview(Guid nodeGuid, PreviewData previewData)
        {
            if (m_Previews.Remove(nodeGuid))
            {
                if (previewData.shader != null)
                    Object.DestroyImmediate(previewData.shader, true);
                if (previewData.renderTexture != null)
                    Object.DestroyImmediate(previewData.renderTexture, true);
                var node = m_Graph.GetNodeFromGuid(nodeGuid);
                if (node != null)
                    node.onModified -= OnNodeModified;
                m_DirtyPreviews.Remove(nodeGuid);
                m_DirtyShaders.Remove(nodeGuid);
                m_TimeDependentPreviews.Remove(nodeGuid);
                previewData.shader = null;
                previewData.renderTexture = null;
                previewData.texture = null;
                previewData.onPreviewChanged = null;
            }
        }

        void ReleaseUnmanagedResources()
        {
            if (m_PreviewMaterial != null)
                Object.DestroyImmediate(m_PreviewMaterial, true);
            m_PreviewMaterial = null;
            if (m_SceneResources != null)
                m_SceneResources.Dispose();
            m_SceneResources = null;
            var previews = m_Previews.ToList();
            foreach (var kvp in previews)
                DestroyPreview(kvp.Key, kvp.Value);
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~PreviewManager()
        {
            ReleaseUnmanagedResources();
        }
    }

    public delegate void OnPreviewChanged();

    public class PreviewData
    {
        public INode node { get; set; }
        public Shader shader { get; set; }
        public Mesh mesh { get; set; }
        public string shaderString { get; set; }
        public PreviewMode previewMode { get; set; }
        public RenderTexture renderTexture { get; set; }
        public Texture texture { get; set; }
        public OnPreviewChanged onPreviewChanged;
    }
}
