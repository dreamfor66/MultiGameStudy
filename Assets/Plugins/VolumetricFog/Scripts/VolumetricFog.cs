﻿//------------------------------------------------------------------------------------------------------------------
// Volumetric Fog & Mist 2
// Created by Kronnect
//------------------------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;

namespace VolumetricFogAndMist2 {

    public enum VolumetricFogShape {
        Box,
        Sphere
    }

    [ExecuteInEditMode]
    public partial class VolumetricFog : MonoBehaviour {

        [HideInInspector] public VolumetricFogProfile profile;

        //[Tooltip("Supports Unity native lights including point and spot lights.")]
        [HideInInspector] public bool enableNativeLights;
        //[Tooltip("Enable fast point lights. This option is much faster than native lights. However, if you enable native lights, this option can't be enabled as point lights are already included in the native lights support.")]
        [HideInInspector] public bool enablePointLights;
        [HideInInspector] public bool enableSpotLights;
        [HideInInspector] public bool enableVoids;

        //[Tooltip("Fades in/out fog effect when reference controller enters the fog volume.")]
        [HideInInspector] public bool enableFade;
        [HideInInspector] public float fadeDistance = 1;
        //[Tooltip("Fades out the fog volume when the reference controller enters the volume. If this option is disabled, the fog appears when the controller enters the vollume.")]
        [HideInInspector] public bool fadeOut;
        //[Tooltip("The controller (player or camera) to check if enters the fog volume.")]
        [HideInInspector] public Transform fadeController;
        //[Tooltip("Enable sub-volume blending.")]
        [HideInInspector] public bool enableSubVolumes;
        //[Tooltip("Allowed subVolumes. If no subvolume is specified, any subvolume entered by this controller will affect this fog volume.")]
        [HideInInspector] public List<VolumetricFogSubVolume> subVolumes;
        //[Tooltip("Shows the fog volume boundary in Game View")]
        [HideInInspector] public bool showBoundary;

        [NonSerialized]
        public MeshRenderer meshRenderer;
        Material fogMat, noiseMat, turbulenceMat;
        Shader fogShader;
        RenderTexture rtNoise, rtTurbulence;
        float turbAcum;
        Vector3 windAcum;
        Vector3 sunDir;
        float dayLight, moonLight;
        List<string> shaderKeywords;
        Texture3D detailTex, refDetailTex;
        Mesh debugMesh;
        Material fogDebugMat;
        VolumetricFogProfile activeProfile, lerpProfile;
        Vector3 lastControllerPosition;
        float alphaMultiplier = 1f;

        bool profileIsInstanced;
        bool requireUpdateMaterial;
        ColorSpace currentAppliedColorSpace;

        /// <summary>
        /// This property will return an instanced copy of the profile and use it for this volumetric fog from now on. Works similarly to Unity's material vs sharedMaterial.
        /// </summary>
        public VolumetricFogProfile settings {
            get {
                if (!profileIsInstanced && profile != null) {
                    profile = Instantiate(profile);
                    profileIsInstanced = true;
                }
                requireUpdateMaterial = true;
                return profile;
            }
            set {
                profile = value;
                profileIsInstanced = false;
            }
        }

        public static List<VolumetricFog> volumetricFogs = new List<VolumetricFog>();

        public Material material => fogMat;



        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init() {
            volumetricFogs.Clear();
        }


        void OnEnable() {
            volumetricFogs.Add(this);
            VolumetricFogManager manager = Tools.CheckMainManager();
            gameObject.layer = manager.fogLayer;
            FogOfWarInit();
            CheckSurfaceCapture();
            UpdateMaterialProperties();
        }

        private void OnDisable() {
            if (volumetricFogs.Contains(this)) volumetricFogs.Remove(this);
            if (profile != null) {
                profile.onSettingsChanged -= UpdateMaterialProperties;
            }
            DisableSurfaceCapture();
        }

        private void OnValidate() {
            UpdateMaterialProperties();
        }

        private void OnDestroy() {
            if (rtNoise != null) {
                rtNoise.Release();
            }
            if (rtTurbulence != null) {
                rtTurbulence.Release();
            }
            if (fogMat != null) {
                DestroyImmediate(fogMat);
                fogMat = null;
            }
            FogOfWarDestroy();
            DisposeSurfaceCapture();
        }

        void OnDrawGizmosSelected() {
            Gizmos.color = new Color(1, 1, 0, 0.75F);
            Gizmos.DrawWireCube(transform.position, transform.lossyScale);
        }

        void LateUpdate() {
            if (fogMat == null || meshRenderer == null || profile == null) return;

            if (requireUpdateMaterial) {
                requireUpdateMaterial = false;
                UpdateMaterialProperties();
            }

            Bounds bounds = meshRenderer.bounds;
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;

            ComputeActiveProfile();

            bool requireApplyProfileSettings = enableFade || enableSubVolumes;
#if UNITY_EDITOR
            if (currentAppliedColorSpace != QualitySettings.activeColorSpace) {
                requireApplyProfileSettings = true;
            }
#endif
            if (requireApplyProfileSettings) {
                ApplyProfileSettings();
            }

            if (activeProfile.shape == VolumetricFogShape.Sphere) {
                Vector3 scale = transform.localScale;
                if (scale.z != scale.x) {
                    scale.z = scale.x;
                    transform.localScale = scale;
                    extents = meshRenderer.bounds.extents;
                }
                extents.x *= extents.x;
            }

            Vector4 border = new Vector4(extents.x * activeProfile.border + 0.0001f, extents.x * (1f - activeProfile.border), extents.z * activeProfile.border + 0.0001f, extents.z * (1f - activeProfile.border));
            if (activeProfile.terrainFit) {
                extents.y = Mathf.Max(extents.y, activeProfile.terrainFogHeight);
            }
            fogMat.SetVector(ShaderParams.BoundsCenter, center);
            fogMat.SetVector(ShaderParams.BoundsExtents, extents);
            fogMat.SetVector(ShaderParams.BoundsBorder, border);
            Vector4 boundsData = new Vector4(activeProfile.verticalOffset, center.y - extents.y, extents.y * 2f, 0);
            fogMat.SetVector(ShaderParams.BoundsData, boundsData);

            VolumetricFogManager globalManager = VolumetricFogManager.instance;
            Light sun = globalManager.sun;
            Color lightColor;
            Color sunColor;
            float sunIntensity;
            float lightDiffusionIntensity;
            if (sun != null) {
                sunDir = -sun.transform.forward;
                sunColor = sun.color;
                sunIntensity = sun.intensity;
                lightDiffusionIntensity = activeProfile.lightDiffusionIntensity;
            } else {
                sunDir = Vector3.up;
                sunColor = Color.white;
                sunIntensity = 1f;
                lightDiffusionIntensity = 0;
            }

            dayLight = 1f + sunDir.y * 2f;
            if (dayLight < 0) dayLight = 0; else if (dayLight > 1f) dayLight = 1f;
            float brightness = activeProfile.brightness;
            float alpha = activeProfile.albedo.a;
            lightColor = sunColor * (dayLight * sunIntensity * brightness * (QualitySettings.activeColorSpace == ColorSpace.Gamma ? 2f : 1.33f));
            lightColor.a = alpha;
            fogMat.SetFloat(ShaderParams.LightDiffusionIntensity, activeProfile.lightDiffusionIntensity);
            fogMat.SetVector(ShaderParams.SunDir, sunDir);

            Light moon = globalManager.moon;
            moonLight = 0;
            if (!enableNativeLights && moon != null) {
                Vector3 moonDir = -moon.transform.forward;
                moonLight = 1f + moonDir.y * 2f;
                if (moonLight < 0) moonLight = 0; else if (moonLight > 1f) moonLight = 1f;
                brightness = activeProfile.brightness;
                alpha = activeProfile.albedo.a;
                lightColor += moon.color * (moonLight * moon.intensity * brightness * 2f);
                lightColor.a = alpha;
            }

            if (enableFade && fadeOut && Application.isPlaying) {
                lightColor.a *= 1f - alphaMultiplier;
            } else {
                lightColor.a *= alphaMultiplier;
            }
            fogMat.SetVector(ShaderParams.LightColor, lightColor);

            windAcum += activeProfile.windDirection * Time.deltaTime;
            windAcum.x %= 10000;
            windAcum.y %= 10000;
            windAcum.z %= 10000;
            fogMat.SetVector(ShaderParams.WindDirection, windAcum);

            transform.rotation = Quaternion.identity;

            UpdateNoise();

            if (enableFogOfWar) {
                UpdateFogOfWar();
            }

            if (showBoundary) {
                if (fogDebugMat == null) {
                    fogDebugMat = new Material(Shader.Find("VolumetricFog/VolumeDebug"));
                }
                if (debugMesh == null) {
                    MeshFilter mf = GetComponent<MeshFilter>();
                    if (mf != null) {
                        debugMesh = mf.sharedMesh;
                    }
                }
                Matrix4x4 m = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
                Graphics.DrawMesh(debugMesh, m, fogDebugMat, 0);
            }

            if (enablePointLights && !enableNativeLights) {
                PointLightManager.usingPointLights = true;
            }

            if (enableVoids) {
                FogVoidManager.usingVoids = true;
            }

            SurfaceCaptureUpdate();
        }


        void UpdateNoise() {

            if (activeProfile == null) return;
            Texture noiseTex = activeProfile.noiseTexture;
            if (noiseTex == null) return;

            if (rtTurbulence == null || rtTurbulence.width != noiseTex.width) {
                RenderTextureDescriptor desc = new RenderTextureDescriptor(noiseTex.width, noiseTex.height, RenderTextureFormat.ARGB32, 0);
                rtTurbulence = new RenderTexture(desc);
                rtTurbulence.wrapMode = TextureWrapMode.Repeat;
            }
            turbAcum += Time.deltaTime * activeProfile.turbulence;
            turbAcum %= 10000;
            turbulenceMat.SetFloat(ShaderParams.TurbulenceAmount, turbAcum);
            turbulenceMat.SetFloat(ShaderParams.NoiseStrength, activeProfile.noiseStrength);
            turbulenceMat.SetFloat(ShaderParams.NoiseFinalMultiplier, activeProfile.noiseFinalMultiplier);
            Graphics.Blit(noiseTex, rtTurbulence, turbulenceMat);

            if (rtNoise == null || rtNoise.width != noiseTex.width) {
                RenderTextureDescriptor desc = new RenderTextureDescriptor(noiseTex.width, noiseTex.height, RenderTextureFormat.ARGB32, 0);
                rtNoise = new RenderTexture(desc);
                rtNoise.wrapMode = TextureWrapMode.Repeat;
            }
            noiseMat.SetColor(ShaderParams.SpecularColor, activeProfile.specularColor);
            noiseMat.SetFloat(ShaderParams.SpecularIntensity, activeProfile.specularIntensity);

            float spec = 1.0001f - activeProfile.specularThreshold;
            float nlighty = sunDir.y > 0 ? (1.0f - sunDir.y) : (1.0f + sunDir.y);
            float nyspec = nlighty / spec;

            noiseMat.SetFloat(ShaderParams.SpecularThreshold, nyspec);
            noiseMat.SetVector(ShaderParams.SunDir, sunDir);

            Color ambientColor = RenderSettings.ambientLight;
            float ambientIntensity = RenderSettings.ambientIntensity;
            Color ambientMultiplied = ambientColor * ambientIntensity;
            float fogIntensity = 1.15f;
            fogIntensity *= (dayLight + moonLight);
            Color textureBaseColor = Color.Lerp(ambientMultiplied, activeProfile.albedo * fogIntensity, fogIntensity);
            noiseMat.SetColor(ShaderParams.Color, textureBaseColor);
            Graphics.Blit(rtTurbulence, rtNoise, noiseMat);

            fogMat.SetTexture(ShaderParams.MainTex, rtNoise);

            Color detailColor = new Color(textureBaseColor.r * 0.5f, textureBaseColor.g * 0.5f, textureBaseColor.b * 0.5f, 0);
            fogMat.SetColor(ShaderParams.DetailColor, detailColor);
        }

        public void UpdateMaterialProperties() {

            if (!gameObject.activeInHierarchy) {
                DisableSurfaceCapture();
                return;
            }

            fadeDistance = Mathf.Max(0.1f, fadeDistance);

            meshRenderer = GetComponent<MeshRenderer>();

            if (profile == null) {
                if (fogMat == null && meshRenderer != null) {
                    fogMat = new Material(Shader.Find("VolumetricFog/Empty"));
                    fogMat.hideFlags = HideFlags.DontSave;
                    meshRenderer.sharedMaterial = fogMat;
                }
                DisableSurfaceCapture();
                return;
            }
            // Subscribe to profile changes
            profile.onSettingsChanged -= UpdateMaterialProperties;
            profile.onSettingsChanged += UpdateMaterialProperties;

            // Subscribe to sub-volume profile changes
            if (subVolumes != null) {
                foreach (VolumetricFogSubVolume subVol in subVolumes) {
                    if (subVol != null && subVol.profile != null) {
                        subVol.profile.onSettingsChanged -= UpdateMaterialProperties;
                        subVol.profile.onSettingsChanged += UpdateMaterialProperties;
                    }
                }
            }

            if (turbulenceMat == null) {
                turbulenceMat = new Material(Shader.Find("VolumetricFog/Turbulence2D"));
            }
            if (noiseMat == null) {
                noiseMat = new Material(Shader.Find("VolumetricFog/Noise2DGen"));
            }

            if (meshRenderer != null) {
                if (fogShader == null) {
                    fogShader = Shader.Find("Rogue/FX/VolumetricFog");
                }
                fogMat = meshRenderer.sharedMaterial;
                if (fogMat == null || fogMat.shader != fogShader) {
                    fogMat = new Material(fogShader);
                    meshRenderer.sharedMaterial = fogMat;
                }
            }

            if (fogMat == null) return;

            profile.ValidateSettings();

            lastControllerPosition.x = float.MaxValue;
            activeProfile = profile;

            ComputeActiveProfile();
            ApplyProfileSettings();
            SurfaceCaptureSupportCheck();
        }

        void ComputeActiveProfile() {

            if (maskEditorEnabled) alphaMultiplier = 0.85f;
            if (Application.isPlaying) {
                if (enableFade || enableSubVolumes) {
                    if (fadeController == null) {
                        Camera cam = Camera.main;
                        if (cam != null) {
                            fadeController = Camera.main.transform;
                        }
                    }
                    if (fadeController != null && lastControllerPosition != fadeController.position) {

                        lastControllerPosition = fadeController.position;
                        activeProfile = profile;
                        alphaMultiplier = 1f;

                        // Self volume
                        if (enableFade) {
                            float t = ComputeVolumeFade(transform, fadeDistance);
                            alphaMultiplier *= t;
                        }

                        // Check sub-volumes
                        if (enableSubVolumes) {
                            int subVolumeCount = VolumetricFogSubVolume.subVolumes.Count;
                            int allowedSubVolumesCount = subVolumes != null ? subVolumes.Count : 0;
                            for (int k = 0; k < subVolumeCount; k++) {
                                VolumetricFogSubVolume subVolume = VolumetricFogSubVolume.subVolumes[k];
                                if (subVolume == null || subVolume.profile == null) continue;
                                if (allowedSubVolumesCount > 0 && !subVolumes.Contains(subVolume)) continue;
                                float t = ComputeVolumeFade(subVolume.transform, subVolume.fadeDistance);
                                if (t > 0) {
                                    if (lerpProfile == null) {
                                        lerpProfile = ScriptableObject.CreateInstance<VolumetricFogProfile>();
                                    }
                                    lerpProfile.Lerp(activeProfile, subVolume.profile, t);
                                    activeProfile = lerpProfile;
                                }
                            }
                        }
                    }
                } else {
                    alphaMultiplier = 1f;
                }
            }

            if (activeProfile == null) {
                activeProfile = profile;
            }
        }

        float ComputeVolumeFade(Transform transform, float fadeDistance) {
            Vector3 diff = transform.position - fadeController.position;
            diff.x = diff.x < 0 ? -diff.x : diff.x;
            diff.y = diff.y < 0 ? -diff.y : diff.y;
            diff.z = diff.z < 0 ? -diff.z : diff.z;
            Vector3 extents = transform.lossyScale * 0.5f;
            Vector3 gap = extents - diff;
            float minDiff = gap.x < gap.y ? gap.x : gap.y;
            minDiff = minDiff < gap.z ? minDiff : gap.z;
            fadeDistance += 0.0001f;
            float t = Mathf.Clamp01(minDiff / fadeDistance);
            return t;
        }


        void ApplyProfileSettings() {
            currentAppliedColorSpace = QualitySettings.activeColorSpace;

            meshRenderer.sortingLayerID = activeProfile.sortingLayerID;
            meshRenderer.sortingOrder = activeProfile.sortingOrder;
            fogMat.renderQueue = activeProfile.renderQueue;
            float noiseScale = 0.1f / activeProfile.noiseScale;
            fogMat.SetFloat(ShaderParams.NoiseScale, noiseScale);
            fogMat.SetFloat(ShaderParams.DeepObscurance, activeProfile.deepObscurance * (currentAppliedColorSpace == ColorSpace.Gamma ? 1f : 1.2f));
            fogMat.SetFloat(ShaderParams.LightDiffusionPower, activeProfile.lightDiffusionPower);
            fogMat.SetFloat(ShaderParams.ShadowIntensity, activeProfile.shadowIntensity);
            fogMat.SetFloat(ShaderParams.Density, activeProfile.density);
            fogMat.SetVector(ShaderParams.RaymarchSettings, new Vector4(activeProfile.raymarchQuality, activeProfile.dithering * 0.01f, activeProfile.jittering, activeProfile.raymarchMinStep));

            if (activeProfile.useDetailNoise) {
                float detailScale = (1f / activeProfile.detailScale) * noiseScale;
                fogMat.SetVector(ShaderParams.DetailData, new Vector4(activeProfile.detailStrength, activeProfile.detailOffset, detailScale, activeProfile.noiseFinalMultiplier));
                fogMat.SetColor(ShaderParams.DetailColor, activeProfile.albedo);
                fogMat.SetFloat(ShaderParams.DetailOffset, activeProfile.detailOffset);
                if ((detailTex == null || refDetailTex != activeProfile.detailTexture) && activeProfile.detailTexture != null) {
                    refDetailTex = activeProfile.detailTexture;
                    Texture3D tex = new Texture3D(activeProfile.detailTexture.width, activeProfile.detailTexture.height, activeProfile.detailTexture.depth, TextureFormat.Alpha8, false);
                    tex.filterMode = FilterMode.Bilinear;
                    Color32[] colors = activeProfile.detailTexture.GetPixels32();
                    for (int k = 0; k < colors.Length; k++) { colors[k].a = colors[k].r; }
                    tex.SetPixels32(colors);
                    tex.Apply();
                    detailTex = tex;
                }
                fogMat.SetTexture(ShaderParams.DetailTexture, detailTex);
            }

            if (shaderKeywords == null) {
                shaderKeywords = new List<string>();
            } else {
                shaderKeywords.Clear();
            }

            bool mustSetDistanceData = activeProfile.distance > 0 || activeProfile.enableDepthGradient;
            if (mustSetDistanceData) {
                fogMat.SetVector(ShaderParams.DistanceData, new Vector4(0, 10f * (1f - activeProfile.distanceFallOff), 1f / (0.0001f + activeProfile.depthGradientMaxDistance * activeProfile.depthGradientMaxDistance), 1f / (0.0001f + activeProfile.distance * activeProfile.distance)));
            }
            if (activeProfile.distance > 0) {
                shaderKeywords.Add(ShaderParams.SKW_DISTANCE);
            }
            if (activeProfile.enableDepthGradient) {
                shaderKeywords.Add(ShaderParams.SKW_DEPTH_GRADIENT);
                fogMat.SetTexture(ShaderParams.DepthGradientTexture, activeProfile.depthGradientTex);
            }
            if (activeProfile.enableHeightGradient) {
                shaderKeywords.Add(ShaderParams.SKW_HEIGHT_GRADIENT);
                fogMat.SetTexture(ShaderParams.HeightGradientTexture, activeProfile.heightGradientTex);
            }
            if (activeProfile.shape == VolumetricFogShape.Box) {
                shaderKeywords.Add(ShaderParams.SKW_SHAPE_BOX);
            } else {
                shaderKeywords.Add(ShaderParams.SKW_SHAPE_SPHERE);
            }
            if (enableNativeLights) {
                shaderKeywords.Add(ShaderParams.SKW_NATIVE_LIGHTS);
            } else if (enablePointLights) {
                shaderKeywords.Add(ShaderParams.SKW_POINT_LIGHTS);
            }
            if (enableVoids) {
                shaderKeywords.Add(ShaderParams.SKW_VOIDS);
            }
            if (activeProfile.receiveShadows) {
                shaderKeywords.Add(ShaderParams.SKW_RECEIVE_SHADOWS);
            }
            if (enableFogOfWar) {
                fogMat.SetTexture(ShaderParams.FogOfWarTexture, fogOfWarTexture);
                UpdateFogOfWarMaterialBoundsProperties();
                shaderKeywords.Add(ShaderParams.SKW_FOW);
            }
            if (activeProfile.useDetailNoise) {
                shaderKeywords.Add(ShaderParams.SKW_DETAIL_NOISE);
            }
            if (activeProfile.terrainFit) {
                shaderKeywords.Add(ShaderParams.SKW_SURFACE);
            }
            fogMat.shaderKeywords = shaderKeywords.ToArray();
        }

        void UpdateFogOfWarMaterialBoundsProperties() {
            Vector3 fogOfWarCenter = anchoredFogOfWarCenter;
            fogMat.SetVector(ShaderParams.FogOfWarCenter, fogOfWarCenter);
            fogMat.SetVector(ShaderParams.FogOfWarSize, fogOfWarSize);
            Vector3 ca = fogOfWarCenter - 0.5f * fogOfWarSize;
            fogMat.SetVector(ShaderParams.FogOfWarCenterAdjusted, new Vector4(ca.x / fogOfWarSize.x, 1f, ca.z / (fogOfWarSize.z + 0.0001f), 0));
        }

        /// <summary>
        /// Issues a refresh of the depth pre-pass alpha clipping renderers list
        /// </summary>
        public static void FindAlphaClippingObjects() {
            DepthRenderPrePassFeature.DepthRenderPass.FindAlphaClippingRenderers();
        }

    }

}
