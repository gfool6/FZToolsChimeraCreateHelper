using System.Diagnostics;
using System.Collections.Specialized;
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using VRC.SDK3.Editor;
using VRC.SDKBase.Editor;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Avatars.Components;
using EUI = FZTools.EditorUtils.UI;
using ELayout = FZTools.EditorUtils.Layout;
using static FZTools.AvatarUtils;

using nadena.dev.modular_avatar.core;
using nadena.dev.modular_avatar.core.editor;

namespace FZTools
{
    public class ChimeraCreateHelper : EditorWindow
    {
        [SerializeField] GameObject headAvatar;
        [SerializeField] GameObject bodyAvatar;

        bool isExtendsFX = true;
        bool isExtendsMenu = true;

        bool? isInstalledMA;
        bool IsInstalledMA
        {
            get
            {
                if (isInstalledMA == null)
                {
                    isInstalledMA = ExternalToolUtils.IsInstalledMA();
                }
                return (bool)isInstalledMA;
            }
        }


        [MenuItem("FZTools/ChimeraCreateHelper(β)")]
        private static void OpenWindow()
        {
            var window = GetWindow<ChimeraCreateHelper>();
            window.titleContent = new GUIContent("ChimeraCreateHelper(β)");
        }

        private void OnGUI()
        {
            ELayout.Horizontal(() =>
            {
                EUI.Space();
                ELayout.Vertical(() =>
                {
                    var text = "以下の機能を提供します\n"
                            + "・指定された頭用アバターと胴体用アバターをModular Avatarでマージします\n"
                            + "・頭の位置を胴体側のView Positionを基準にざっくりと合わせます（調整は自ら行ってください）\n"
                            + "・Eye lookやvisemeの設定なども自動で修正します\n";
                    EUI.InfoBox(text);
                    if (!IsInstalledMA)
                    {
                        EUI.ErrorBox("Modular Avatarがインストールされていません。\nこのツールはModular Avatarが前提となります。");
                        return;
                    }
                    EUI.Label("Head Avatar");
                    EUI.ChangeCheck(
                        () => EUI.ObjectField<GameObject>(ref headAvatar),
                        () =>
                        {
                        });
                    EUI.Space();

                    EUI.Label("Body Avatar");
                    EUI.ChangeCheck(
                        () => EUI.ObjectField<GameObject>(ref bodyAvatar),
                        () =>
                        {
                        });
                    EUI.Space();

                    EUI.ToggleWithLabel(ref isExtendsFX, "表情を引き継ぎ（頭）");
                    EUI.ToggleWithLabel(ref isExtendsMenu, "メニューを引き継ぎ（頭）");

                    EUI.Space(2);
                    using (new EditorGUI.DisabledScope(!IsInstalledMA || headAvatar == null || bodyAvatar == null))
                    {
                        EUI.Button("合成", Fusion);
                    }
                });
            });
        }

        private void Fusion()
        {
            if (headAvatar == null || bodyAvatar == null)
            {
                UnityEngine.Debug.LogError("頭用アバターと胴体用アバターの両方を指定してください");
                return;
            }

            var headDescriptor = headAvatar.GetAvatarDescriptor();
            var bodyDescriptor = bodyAvatar.GetAvatarDescriptor();

            if (headDescriptor == null || bodyDescriptor == null)
            {
                UnityEngine.Debug.LogError("頭用アバターと胴体用アバターの両方にVRCAvatarDescriptorが必要です");
                return;
            }

            AlignHeadPositionFromBody(headDescriptor, bodyDescriptor);
            HideBodyMeshes(headDescriptor);
            HideHeadMeshes(bodyDescriptor);
            CopyDescriptorHead2Body(headDescriptor, bodyDescriptor);
            if (isExtendsFX)
            {
                ConvertFaceFX2MAMergeAnimator(headDescriptor);
            }
            if (isExtendsMenu)
            {
                ConvertExpressions2MAMenuAndParam(headDescriptor);
            }
            Combine(headDescriptor, bodyDescriptor);
        }

        private void AlignHeadPositionFromBody(VRCAvatarDescriptor headDescriptor, VRCAvatarDescriptor bodyDescriptor)
        {
            Vector3 headViewPosition = headDescriptor.ViewPosition;
            Vector3 bodyViewPosition = bodyDescriptor.ViewPosition;

            // bodyとheadのViewPosition.yの比率を求めて、head側のScaleを調整
            float yRatio = bodyViewPosition.y / headViewPosition.y;
            headDescriptor.transform.localScale = headDescriptor.transform.localScale * yRatio;

            // neckのWorldPositionを基準にheadのPositionを調整
            Animator headAvatarAnimator = headAvatar.GetComponent<Animator>();
            var headBoneHead = headAvatarAnimator.GetBoneTransform(HumanBodyBones.Head);
            Animator bodyAvatarAnimator = bodyAvatar.GetComponent<Animator>();
            var headBoneBody = bodyAvatarAnimator.GetBoneTransform(HumanBodyBones.Head);

            float zOffset = headBoneBody.position.z - headBoneHead.position.z;
            headDescriptor.transform.position = headDescriptor.transform.position + new Vector3(0, 0, zOffset);
            bodyDescriptor.ViewPosition = bodyDescriptor.ViewPosition + new Vector3(0, 0, zOffset);
        }

        private void CopyDescriptorHead2Body(VRCAvatarDescriptor headDescriptor, VRCAvatarDescriptor bodyDescriptor)
        {
            // LipSyncで参照してるFaceMeshをhead→bodyにコピー
            var lipSyncUsesFaceMesh = bodyDescriptor.lipSync == VRCAvatarDescriptor.LipSyncStyle.VisemeBlendShape || bodyDescriptor.lipSync == VRCAvatarDescriptor.LipSyncStyle.JawFlapBlendShape;
            if (lipSyncUsesFaceMesh)
            {
                bodyDescriptor.VisemeSkinnedMesh = headDescriptor.VisemeSkinnedMesh;
            }

            // Eyelids側のFaceMeshコピー
            var eyelidUsesFaceMesh = bodyDescriptor.customEyeLookSettings.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes;
            if (eyelidUsesFaceMesh)
            {
                bodyDescriptor.customEyeLookSettings.eyelidsSkinnedMesh = headDescriptor.customEyeLookSettings.eyelidsSkinnedMesh;
            }

            // Eyesで参照してる左右目ボーンをhead→bodyにコピー
            bodyDescriptor.customEyeLookSettings.leftEye = headDescriptor.customEyeLookSettings.leftEye;
            bodyDescriptor.customEyeLookSettings.rightEye = headDescriptor.customEyeLookSettings.rightEye;
        }

        private void HideBodyMeshes(VRCAvatarDescriptor headDescriptor)
        {
            // 顔側：
            // 全メッシュ取得→非表示にし、顔メッシュだけ表示する
            var faceMesh = headDescriptor.GetVRCAvatarFaceMeshRenderer();
            headDescriptor.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToList().ForEach(smr => smr.gameObject.SetActive(false));
            faceMesh.gameObject.SetActive(true);
            // もしHairMeshがあればそれも表示にする。SkinnedMeshRendererを持つgameObjectのnameに"hair"が含まれるものを探す
            headDescriptor.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                                            .Where(smr => IsHeadParts(smr.gameObject.name)).ToList()
                                            .ForEach(smr => smr.gameObject.SetActive(true));
        }

        private void HideHeadMeshes(VRCAvatarDescriptor bodyDescriptor)
        {
            var faceMesh = bodyDescriptor.GetVRCAvatarFaceMeshRenderer();
            faceMesh.gameObject.SetActive(false);
            bodyDescriptor.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                                            .Where(smr => IsHeadParts(smr.gameObject.name)).ToList()
                                            .ForEach(smr => smr.gameObject.SetActive(false));
        }

        private void ConvertFaceFX2MAMergeAnimator(VRCAvatarDescriptor headDescriptor)
        {
            var controller = headDescriptor.GetPlayableLayerController(VRCAvatarDescriptor.AnimLayerType.FX);
            var mergeAnimator = headDescriptor.gameObject.AddComponent<ModularAvatarMergeAnimator>();
            mergeAnimator.animator = controller;
            mergeAnimator.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            mergeAnimator.pathMode = MergeAnimatorPathMode.Relative;
            mergeAnimator.mergeAnimatorMode = MergeAnimatorMode.Append;
        }

        private void ConvertExpressions2MAMenuAndParam(VRCAvatarDescriptor headDescriptor)
        {
            var menuInstaller = headDescriptor.gameObject.AddComponent<ModularAvatarMenuInstaller>();
            menuInstaller.menuToAppend = headDescriptor.expressionsMenu;

            // TODO できればExParamを分解して入れ直さずに直でアセットからインポートさせたい
            var maParameters = headDescriptor.gameObject.AddComponent<ModularAvatarParameters>();
            var exParam = headDescriptor.expressionParameters;
            foreach (var param in exParam.parameters)
            {
                maParameters.parameters.Add(new ParameterConfig()
                {
                    nameOrPrefix = param.name,
                    remapTo = "",
                    internalParameter = false,
                    isPrefix = false,
                    syncType = param.valueType switch
                    {
                        VRCExpressionParameters.ValueType.Bool => ParameterSyncType.Bool,
                        VRCExpressionParameters.ValueType.Float => ParameterSyncType.Float,
                        VRCExpressionParameters.ValueType.Int => ParameterSyncType.Int,
                        _ => ParameterSyncType.Float,
                    },
                    localOnly = !param.networkSynced,
                    defaultValue = param.defaultValue,
                    saved = param.saved
                });
            }
        }

        private void Combine(VRCAvatarDescriptor headDescriptor, VRCAvatarDescriptor bodyDescriptor)
        {
            // Head AvatarにModularAvatarコンポーネントを追加
            // HeadのDescriptorを削除
            DestroyImmediate(headAvatar.GetComponent<VRCAvatarDescriptor>());
            DestroyImmediate(headAvatar.GetComponent<VRC.Core.PipelineManager>());

            // setup 
            headAvatar.transform.SetParent(bodyAvatar.transform);
            SetupOutfit.SetupOutfitUI(headAvatar);
        }

        private bool IsHeadParts(string gameObjectName)
        {
            var headPartsKeywords = new string[] { "face", "head", "hair", "horn", "ear" };
            var name = gameObjectName.ToLower();
            foreach (var parts in headPartsKeywords)
            {
                if (name.Contains(parts))
                {
                    return true;
                }
            }
            return false;
        }
    }
}