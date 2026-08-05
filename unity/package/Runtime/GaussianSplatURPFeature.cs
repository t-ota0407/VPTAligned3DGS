// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting.Runtime
{
    // ReSharper disable once InconsistentNaming
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";
            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);
            static readonly int s_gaussianSplatRT = Shader.PropertyToID(GaussianSplatRTName);
            static readonly int s_gsDepthTex      = Shader.PropertyToID("_GS_DepthTex");
            static readonly int s_gsHasDepthTex   = Shader.PropertyToID("_GS_HasDepthTex");

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle GaussianSplatRT;
                // Editor のみ: ソフトウェア深度テスト用
                internal TextureHandle DepthTexture;
                internal bool HasDepthTexture;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var cameraData   = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples     = 1;
                rtDesc.graphicsFormat  = GraphicsFormat.R16G16B16A16_SFloat;
                var textureHandle = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph, rtDesc, GaussianSplatRTName, true);

                passData.CameraData      = cameraData;
                passData.SourceTexture   = resourceData.activeColorTexture;
                passData.GaussianSplatRT = textureHandle;

                // Editor / 非Androidビルド: 深度テクスチャを取得してソフトウェア深度テストに使う
                // (copyDepthMode=AfterOpaques + supportsCameraDepthTexture=true で確実に有効)
#if !UNITY_ANDROID || UNITY_EDITOR
                var depthTex = resourceData.cameraDepthTexture;
                passData.HasDepthTexture = depthTex.IsValid();
                passData.DepthTexture    = depthTex;
                if (passData.HasDepthTexture)
                    builder.UseTexture(passData.DepthTexture, AccessFlags.Read);
#else
                passData.HasDepthTexture = false;
#endif

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(textureHandle, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(cmd, s_profilingSampler);

                    cmd.SetGlobalTexture(s_gaussianSplatRT, data.GaussianSplatRT);

                    // Fix5: FlipProjectionIfBackbuffer 抑制
                    cmd.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture,
                        data.GaussianSplatRT);

                    // 深度テクスチャをシェーダーに渡す（Editor のみ有効）
                    if (data.HasDepthTexture)
                    {
                        cmd.SetGlobalTexture(s_gsDepthTex, data.DepthTexture);
                        cmd.SetGlobalFloat(s_gsHasDepthTex, 1.0f);
                    }
                    else
                    {
                        cmd.SetGlobalFloat(s_gsHasDepthTex, 0.0f);
                    }

                    // Fix7: GaussianSplatRT に depth バッファをバインドしない
                    CoreUtils.SetRenderTarget(cmd, data.GaussianSplatRT,
                        ClearFlag.Color, Color.clear);

                    Material matComposite = GaussianSplatRenderSystem.instance
                        .SortAndRenderSplats(data.CameraData.camera, cmd);

                    cmd.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                    Blitter.BlitCameraTexture(cmd, data.GaussianSplatRT,
                        data.SourceTexture, matComposite, 0);
                    cmd.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                });
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass
            {
                // Android デバイスビルド:
                //   BeforeRenderingOpaques → オパーク描画が GS を自然に上書き
                //   (Vulkan の render pass load/store op により GS が保持される)
                //
                // Editor / 非Androidビルド:
                //   BeforeRenderingTransparents → オパーク描画後に GS を描画。
                //   コンポジットシェーダーで _GS_DepthTex を使ってソフトウェア深度テストし、
                //   Y_Bot/Cube がある場所では GS を discard する。
#if UNITY_ANDROID && !UNITY_EDITOR
                renderPassEvent = RenderPassEvent.BeforeRenderingOpaques
#else
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
#endif
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;
            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera) return;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing) { m_Pass = null; }
    }
}

#endif // #if GS_ENABLE_URP
