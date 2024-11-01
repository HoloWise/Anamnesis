﻿// © Anamnesis.
// Licensed under the MIT license.

namespace Anamnesis.Files;

using Anamnesis.Actor;
using Anamnesis.Memory;
using Anamnesis.Posing;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

public class PoseFile : JsonFileBase
{
	[Flags]
	public enum Mode
	{
		None = 0,
		Rotation = 1,
		Scale = 2,
		Position = 4,
		WorldRotation = 8,
		WorldScale = 16,

		All = Rotation | Scale | Position | WorldRotation | WorldScale,
	}

	public enum BoneProcessingModes
	{
		Ignore,
		KeepRelative,
		FullLoad,
	}

	public override string FileExtension => ".pose";
	public override string TypeName => "Anamnesis Pose";

	public Vector3? Position { get; set; }
	public Quaternion? Rotation { get; set; }
	public Vector3? Scale { get; set; }

	public Dictionary<string, Bone?>? Bones { get; set; }

	public static BoneProcessingModes GetBoneMode(ActorMemory? actor, SkeletonVisual3d? skeleton, string boneName)
	{
		if (boneName == "n_root")
			return BoneProcessingModes.Ignore;

		// Special case for elezen ears as they cannot use other races ear values.
		if (actor?.Customize?.Race == ActorCustomizeMemory.Races.Elezen)
		{
			// append '_elezen' to both ear bones.
			if (boneName == "j_mimi_l" || boneName == "j_mimi_r")
			{
				return BoneProcessingModes.KeepRelative;
			}
		}

		return BoneProcessingModes.FullLoad;
	}

	public static async Task<DirectoryInfo?> Save(DirectoryInfo? dir, ActorMemory? actor, SkeletonVisual3d? skeleton, HashSet<string>? bones = null, bool editMeta = false)
	{
		if (actor == null || skeleton == null)
			return null;

		SaveResult result = await FileService.Save<PoseFile>(dir, FileService.DefaultPoseDirectory);

		if (result.Path == null)
			return null;

		PoseFile file = new PoseFile();
		file.WriteToFile(actor, skeleton, bones);

		using FileStream stream = new FileStream(result.Path.FullName, FileMode.Create);
		file.Serialize(stream);

		if (editMeta)
			FileMetaEditor.Show(result.Path, file);

		return result.Directory;
	}

	public void WriteToFile(ActorMemory actor, SkeletonVisual3d skeleton, HashSet<string>? bones)
	{
		if (actor.ModelObject == null || actor.ModelObject.Transform == null)
			throw new Exception("No model in actor");

		if (skeleton == null || skeleton.Bones == null)
			throw new Exception("No skeleton in actor");

		this.Rotation = actor.ModelObject.Transform.Rotation;
		this.Position = actor.ModelObject.Transform.Position;
		this.Scale = actor.ModelObject.Transform.Scale;

		this.Bones = new Dictionary<string, Bone?>();

		foreach (BoneVisual3d bone in skeleton.Bones.Values)
		{
			if (bones != null && !bones.Contains(bone.BoneName))
				continue;

			this.Bones.Add(bone.BoneName, new Bone(bone));
		}
	}

	public async Task Apply(ActorMemory actor, SkeletonVisual3d skeleton, HashSet<string>? bones, Mode mode, bool doFacialExpressionHack)
	{
		if (actor == null)
			throw new ArgumentNullException(nameof(actor));

		if (actor.ModelObject == null || actor.ModelObject.Transform == null)
			throw new Exception("Actor has no model");

		if (actor.ModelObject.Skeleton == null)
			throw new Exception("Actor model has no skeleton");

		if (this.Bones == null)
			return;

		/*
		// Positions would be nice... but they are per-map!
		if (this.Position != null)
			actor.ModelObject.Transform.Position = this.Position;
		*/

		if (bones == null)
		{
			if (mode.HasFlag(Mode.WorldScale) && this.Scale != null)
				actor.ModelObject.Transform.Scale = (Vector3)this.Scale;

			if (mode.HasFlag(Mode.WorldRotation) && this.Rotation != null)
				actor.ModelObject.Transform.Rotation = (Quaternion)this.Rotation;
		}

		SkeletonMemory? skeletonMem = actor.ModelObject.Skeleton;

		PoseService.Instance.SetEnabled(true);
		PoseService.Instance.CanEdit = false;
		skeletonMem.PauseSynchronization = true;
		await Task.Delay(100);

		// Create a back up of the relative rotations of every bones
		Dictionary<string, Quaternion> unPosedBoneRotations = new Dictionary<string, Quaternion>();
		foreach ((string name, BoneVisual3d bone) in skeleton.Bones)
		{
			if (GetBoneMode(actor, skeleton, name) == BoneProcessingModes.Ignore)
				continue;

			unPosedBoneRotations.Add(name, bone.Rotation);
		}

		// Facial expressions hack:
		// Since all facial bones are parented to the head, if we load the head rotation from
		// the pose that matches the expression, it wont break.
		// We then just set the head back to where it should be afterwards.
		// We can skip this if we actually intend to pose the head
		// in the case of posing by selected bones or by body.
		BoneVisual3d? headBone = skeleton.GetBone("j_kao");
		Quaternion? originalHeadRotation = null;
		Vector3? originalHeadPosition = null;
		if (doFacialExpressionHack && bones != null && bones.Contains("j_kao"))
		{
			if (headBone == null)
				throw new Exception("Unable to find head (j_kao) bone.");

			headBone.Synchronize();
			originalHeadRotation = headBone?.Rotation;
			originalHeadPosition = headBone?.Position;
		}

		// Apply all transforms a few times to ensure parent-inherited values are caluclated correctly, and to ensure
		// we dont end up with some values read during a ffxiv frame update.
		for (int i = 0; i < 3; i++)
		{
			foreach ((string name, Bone? savedBone) in this.Bones)
			{
				if (savedBone == null)
					continue;

				string boneName = name;
				string? modernName = LegacyBoneNameConverter.GetModernName(name);
				if (modernName != null)
					boneName = modernName;

				// Don't apply bones that cant be serialized.
				if (GetBoneMode(actor, skeleton, boneName) != BoneProcessingModes.FullLoad)
					continue;

				BoneVisual3d? bone = skeleton.GetBone(boneName);

				if (bone == null)
				{
					Log.Warning($"Bone: \"{boneName}\" not found");
					continue;
				}

				if (bones != null && !bones.Contains(boneName))
					continue;

				// Remove this bone from the relative rotations backup
				unPosedBoneRotations.Remove(boneName);

				foreach (TransformMemory transformMemory in bone.TransformMemories)
				{
					if (savedBone.Position != null && mode.HasFlag(Mode.Position) && bone.CanTranslate)
					{
						transformMemory.Position = (Vector3)savedBone.Position;
					}

					if (savedBone.Rotation != null && mode.HasFlag(Mode.Rotation) && bone.CanRotate)
					{
						transformMemory.Rotation = (Quaternion)savedBone.Rotation;
					}

					if (savedBone.Scale != null && mode.HasFlag(Mode.Scale) && bone.CanScale)
					{
						transformMemory.Scale = (Vector3)savedBone.Scale;
					}
				}

				bone.ReadTransform();
				bone.WriteTransform(skeleton, false);
			}

			await Task.Delay(1);
		}

		// Restore the head bone rotation if we were only loading an expression
		if (headBone != null && originalHeadRotation != null && originalHeadPosition != null)
		{
			headBone.Rotation = (Quaternion)originalHeadRotation;
			headBone.Position = (Vector3)originalHeadPosition;
			headBone.WriteTransform(skeleton, true);
		}

		// Restore the relative rotations of any bones that we did not explicitly write to.
		foreach ((string name, Quaternion rotation) in unPosedBoneRotations)
		{
			BoneVisual3d? bone = skeleton.GetBone(name);

			if (bone == null)
				continue;

			bone.Rotation = rotation;
			bone.WriteTransform(skeleton, false);
		}

		await Task.Delay(100);

		skeletonMem.PauseSynchronization = false;
		skeletonMem.Synchronize();

		PoseService.Instance.CanEdit = true;
	}

	public bool IsPreDTPoseFile()
	{
		if (this.Bones == null)
			return false;

		// Check to see if we have *any* face bones at all.
		// If not, then the file is backwards compatibile.
		bool hasFaceBones = false;
		foreach ((string name, Bone? bone) in this.Bones)
		{
			if (name.StartsWith("j_f_"))
			{
				hasFaceBones = true;
				break;
			}
		}

		if (!hasFaceBones)
			return false;

		// Looking for the tongue-A bone, a new bone common to all races and genders added in DT.
		// If we dont have it, we are assumed to be a pre-DT pose file.
		// This doesn't account for users manually editing the JSON.
		if (!this.Bones.ContainsKey("j_f_bero_01"))
			return true;

		return false;
	}

	[Serializable]
	public class Bone
	{
		public Bone()
		{
		}

		public Bone(BoneVisual3d boneVisual)
		{
			this.Position = boneVisual.TransformMemory.Position;
			this.Rotation = boneVisual.TransformMemory.Rotation;
			this.Scale = boneVisual.TransformMemory.Scale;
		}

		public Vector3? Position { get; set; }
		public Quaternion? Rotation { get; set; }
		public Vector3? Scale { get; set; }
	}
}
