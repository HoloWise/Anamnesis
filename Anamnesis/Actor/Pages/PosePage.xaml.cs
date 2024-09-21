﻿// © Anamnesis.
// Licensed under the MIT license.

namespace Anamnesis.Actor.Pages;

using Anamnesis.Actor.Views;
using Anamnesis.Files;
using Anamnesis.GUI.Dialogs;
using Anamnesis.Memory;
using Anamnesis.Services;
using PropertyChanged;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using XivToolsWpf;
using XivToolsWpf.Math3D.Extensions;
using CmQuaternion = System.Numerics.Quaternion;

/// <summary>
/// Interaction logic for CharacterPoseView.xaml.
/// </summary>
[AddINotifyPropertyChangedInterface]
public partial class PosePage : UserControl
{
	public const double DragThreshold = 20;

	public HashSet<BoneView> BoneViews = new HashSet<BoneView>();

	private static DirectoryInfo? lastLoadDir;
	private static DirectoryInfo? lastSaveDir;

	private bool isLeftMouseButtonDownOnWindow;
	private bool isDragging;
	private Point origMouseDownPoint;

	private Task? writeSkeletonTask;

	public PosePage()
	{
		this.InitializeComponent();

		this.ContentArea.DataContext = this;

		HistoryService.OnHistoryApplied += this.OnHistoryApplied;
	}

	public SettingsService SettingsService => SettingsService.Instance;
	public GposeService GposeService => GposeService.Instance;
	public PoseService PoseService => PoseService.Instance;
	public TargetService TargetService => TargetService.Instance;

	public bool IsFlipping { get; private set; }
	public ActorMemory? Actor { get; private set; }
	public SkeletonVisual3d? Skeleton { get; private set; }

	private static ILogger Log => Serilog.Log.ForContext<PosePage>();

	public List<BoneView> GetBoneViews(BoneVisual3d bone)
	{
		List<BoneView> results = new List<BoneView>();
		foreach (BoneView boneView in this.BoneViews)
		{
			if (boneView.Bone == bone)
			{
				results.Add(boneView);
			}
		}

		return results;
	}

	/* Basic Idea:
	 * get mirrored quat of targetBone
	 * check if its a 'left' bone
	 *- if it is:
	 *          - get the mirrored quat of the corresponding right bone
	 *          - store the first quat (from left bone) on the right bone
	 *          - store the second quat (from right bone) on the left bone
	 *      - if not:
	 *          - store the quat on the target bone
	 *  - recursively flip on all child bones
	 */

	// TODO: This doesn't seem to be working correctly after the skeleton upgrade. not sure why...
	private void FlipBone(BoneVisual3d? targetBone, bool shouldFlip = true)
	{
		if (targetBone != null)
		{
			CmQuaternion newRotation = targetBone.TransformMemory.Rotation.Mirror(); // character-relative transform
			if (shouldFlip && targetBone.BoneName.EndsWith("_l"))
			{
				string rightBoneString = targetBone.BoneName.Substring(0, targetBone.BoneName.Length - 2) + "_r"; // removes the "_l" and replaces it with "_r"
				/*	Useful debug lines to make sure the correct bones are grabbed...
				 *	Log.Information("flipping: " + targetBone.BoneName);
				 *	Log.Information("right flip target: " + rightBoneString); */
				BoneVisual3d? rightBone = targetBone.Skeleton.GetBone(rightBoneString);
				if (rightBone != null)
				{
					CmQuaternion rightRot = rightBone.TransformMemory.Rotation.Mirror();
					foreach (TransformMemory transformMemory in targetBone.TransformMemories)
					{
						transformMemory.Rotation = rightRot;
					}

					foreach (TransformMemory transformMemory in rightBone.TransformMemories)
					{
						transformMemory.Rotation = newRotation;
					}
				}
				else
				{
					Log.Warning("could not find right bone of: " + targetBone.BoneName);
				}
			}
			else if (shouldFlip && targetBone.BoneName.EndsWith("_r"))
			{
				// do nothing so it doesn't revert...
			}
			else
			{
				foreach (TransformMemory transformMemory in targetBone.TransformMemories)
				{
					transformMemory.Rotation = newRotation;
				}
			}

			if (PoseService.Instance.EnableParenting)
			{
				foreach (Visual3D? child in targetBone.Children)
				{
					if (child is BoneVisual3d childBone)
					{
						this.FlipBone(childBone, shouldFlip);
					}
				}
			}
		}
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		this.OnDataContextChanged(null, default);

		PoseService.EnabledChanged += this.OnPoseServiceEnabledChanged;
		this.PoseService.PropertyChanged += this.PoseService_PropertyChanged;
	}

	private void PoseService_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		Application.Current?.Dispatcher.Invoke(() =>
		{
			////if (this.Skeleton != null && !this.PoseService.CanEdit)
			////	this.Skeleton.CurrentBone = null;

			this.Skeleton?.Reselect();
			this.Skeleton?.ReadTranforms();
		});
	}

	private void OnPoseServiceEnabledChanged(bool value)
	{
		if (!value)
		{
			this.OnClearClicked(null, null);
		}
		else
		{
			Application.Current?.Dispatcher.Invoke(() =>
			{
				this.Skeleton?.ReadTranforms();
			});
		}
	}

	private async void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
	{
		ActorMemory? newActor = this.DataContext as ActorMemory;

		// don't do all this work unless we need to.
		if (this.Actor == newActor)
			return;

		if (this.Actor?.ModelObject != null)
		{
			this.Actor.ModelObject.PropertyChanged -= this.OnModelObjectChanged;
		}

		if (newActor?.ModelObject != null)
		{
			newActor.ModelObject.PropertyChanged += this.OnModelObjectChanged;
		}

		this.Actor = newActor;

		await this.Refresh();
	}

	private async void OnModelObjectChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ActorModelMemory.Skeleton))
		{
			await this.Refresh();
		}
	}

	private async Task Refresh()
	{
		await Dispatch.MainThread();

		this.ThreeDView.DataContext = null;
		this.BodyGuiView.DataContext = null;
		this.FaceGuiView.DataContext = null;
		this.MatrixView.DataContext = null;

		this.BoneViews.Clear();

		if (this.Actor == null || this.Actor.ModelObject == null)
		{
			this.Skeleton?.Clear();
			this.Skeleton = null;
			return;
		}

		try
		{
			if (this.Skeleton == null)
				this.Skeleton = new SkeletonVisual3d();

			await this.Skeleton.SetActor(this.Actor);

			this.ThreeDView.DataContext = this.Skeleton;
			this.BodyGuiView.DataContext = this.Skeleton;
			this.FaceGuiView.DataContext = this.Skeleton;
			this.MatrixView.DataContext = this.Skeleton;

			if (this.writeSkeletonTask == null || this.writeSkeletonTask.IsCompleted)
			{
				this.writeSkeletonTask = Task.Run(this.WriteSkeletonThread);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to bind skeleton to view");
		}
	}

	private async void OnImportClicked(object sender, RoutedEventArgs e)
	{
		await this.ImportPose(false, PoseFile.Mode.Rotation, true);
		this.Skeleton?.ClearSelection();
	}

	private async void OnImportScaleClicked(object sender, RoutedEventArgs e)
	{
		await this.ImportPose(false, PoseFile.Mode.Scale, true);
	}

	private async void OnImportSelectedClicked(object sender, RoutedEventArgs e)
	{
		await this.ImportPose(true, PoseFile.Mode.Rotation, false);
	}

	private async void OnImportAllClicked(object sender, RoutedEventArgs e)
	{
		await this.ImportPose(false, PoseFile.Mode.All, true);
	}

	private async void OnImportBodyClicked(object sender, RoutedEventArgs e)
	{
		if (this.Skeleton == null)
			return;

		this.Skeleton.SelectBody();

		await this.ImportPose(true, PoseFile.Mode.Rotation, false);
		this.Skeleton.ClearSelection();
	}

	private async void OnImportExpressionClicked(object sender, RoutedEventArgs e)
	{
		if (this.Skeleton == null)
			return;

		if (this.PoseService.FreezePositions)
		{
			bool? result = await GenericDialog.ShowLocalizedAsync("Pose_WarningExpresionPositions", "Common_Confirm", MessageBoxButton.OKCancel);

			if (result != true)
			{
				return;
			}
		}

		this.Skeleton.SelectHead();
		await this.ImportPose(true, PoseFile.Mode.Rotation | PoseFile.Mode.Scale | PoseFile.Mode.Position, true);
		this.Skeleton.ClearSelection();
	}

	// We want to skip doing the "Facial Expression Hack" if we're posing by selected bones or by body.
	// Otherwise, the head won't pose as intended and will return to its original position.
	// Not quite !selectedOnly, due to posing by expression actually needing the hack.
	private async Task ImportPose(bool selectionOnly, PoseFile.Mode mode, bool doFacialExpressionHack)
	{
		try
		{
			if (this.Actor == null || this.Skeleton == null)
				return;

			bool initialFreezeScale = PoseService.Instance.FreezeScale;
			bool initialFreezePosition = PoseService.Instance.FreezePositions;

			PoseService.Instance.SetEnabled(true);
			PoseService.Instance.FreezeScale |= mode.HasFlag(PoseFile.Mode.Scale);
			PoseService.Instance.FreezeRotation |= mode.HasFlag(PoseFile.Mode.Rotation);
			PoseService.Instance.FreezePositions |= mode.HasFlag(PoseFile.Mode.Position);

			Type[] types = new[]
			{
					typeof(PoseFile),
					typeof(CmToolPoseFile),
			};

			Shortcut[] shortcuts = new[]
			{
					FileService.DefaultPoseDirectory,
					FileService.StandardPoseDirectory,
					FileService.CMToolPoseSaveDir,
			};

			OpenResult result = await FileService.Open(lastLoadDir, shortcuts, types);

			if (result.File == null)
				return;

			lastLoadDir = result.Directory;

			if (result.File is CmToolPoseFile legacyFile)
				result.File = legacyFile.Upgrade(this.Actor.Customize?.Race ?? ActorCustomizeMemory.Races.Hyur);

			if (result.File is PoseFile poseFile)
			{
				HashSet<string>? bones = null;

				// If we are loading the rotation of all bones in a pre-DT pose file, let's not load the face bones.
				// This acts as if we were loading by body pose, which loads the rotations of just the body bones.
				if (poseFile.IsPreDTPoseFile() && !selectionOnly && mode == PoseFile.Mode.Rotation)
				{
					this.Skeleton.SelectBody();
					selectionOnly = true;
				}

				// If we are loading an expression, which is done via selected bones and all three pose modes, we should warn the user if:
				// We are loading a pre-DT pose file on to a DT-updated face, OR
				// We are loading a DT pose file on to a non-updated face.
				// Our entry point is loading by expression, which is done via selected bones and all three pose modes.
				if (selectionOnly && mode == (PoseFile.Mode.Rotation | PoseFile.Mode.Scale | PoseFile.Mode.Position))
				{
					string dialogMsgKey = "Pose_WarningExpresionOldOnNew";
					bool doPreDtExpressionPoseModeOnOK = false;
					bool showDialog = true;

					if (poseFile.IsPreDTPoseFile() && !this.Skeleton.HasPreDTFace)
					{
						// Old pose, and new face. Warn, load as EW expression if OK.
						doPreDtExpressionPoseModeOnOK = true;
					}
					else if (!poseFile.IsPreDTPoseFile() && this.Skeleton.HasPreDTFace)
					{
						// New pose and old face. Warn, load as DT expression if OK.
						dialogMsgKey = "Pose_WarningExpresionNewOnOld";
					}
					else if (poseFile.IsPreDTPoseFile() && this.Skeleton.HasPreDTFace)
					{
						// Old pose and old face. Don't warn, load as EW expression.
						showDialog = false;
						doPreDtExpressionPoseModeOnOK = true;
					}
					else
					{
						// New pose and new face. Don't warn. Everything is OK.
						showDialog = false;
					}

					if (showDialog)
					{
						bool? dialogResult = await GenericDialog.ShowLocalizedAsync(dialogMsgKey, "Common_Confirm", MessageBoxButton.OKCancel);
						if (dialogResult != true)
						{
							PoseService.Instance.FreezeScale = initialFreezeScale;
							PoseService.Instance.FreezePositions = initialFreezePosition;
							return;
						}
					}

					// Prior to DT, we loaded expressions via Rotation and Scale only.
					if (doPreDtExpressionPoseModeOnOK)
					{
						PoseService.Instance.FreezePositions = initialFreezePosition;
						mode = PoseFile.Mode.Rotation | PoseFile.Mode.Scale;
					}
				}

				if (selectionOnly)
				{
					bones = new HashSet<string>();

					foreach ((string name, BoneVisual3d visual) in this.Skeleton.Bones)
					{
						if (this.Skeleton.GetIsBoneSelected(visual))
						{
							bones.Add(name);
						}
					}
				}

				await poseFile.Apply(this.Actor, this.Skeleton, bones, mode, doFacialExpressionHack);
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Failed to load pose file");
		}
	}

	private async void OnExportClicked(object sender, RoutedEventArgs e)
	{
		lastSaveDir = await PoseFile.Save(lastSaveDir, this.Actor, this.Skeleton);
	}

	private async void OnExportMetaClicked(object sender, RoutedEventArgs e)
	{
		lastSaveDir = await PoseFile.Save(lastSaveDir, this.Actor, this.Skeleton, null, true);
	}

	private async void OnExportSelectedClicked(object sender, RoutedEventArgs e)
	{
		if (this.Skeleton == null)
			return;

		HashSet<string> bones = new HashSet<string>();
		foreach (BoneVisual3d bone in this.Skeleton.SelectedBones)
		{
			bones.Add(bone.BoneName);
		}

		lastSaveDir = await PoseFile.Save(lastSaveDir, this.Actor, this.Skeleton, bones);
	}

	private void OnViewChanged(object sender, SelectionChangedEventArgs e)
	{
		int selected = this.ViewSelector.SelectedIndex;

		if (this.BodyGuiView == null)
			return;

		this.BodyGuiView.Visibility = selected == 0 ? Visibility.Visible : Visibility.Collapsed;
		this.FaceGuiView.Visibility = selected == 1 ? Visibility.Visible : Visibility.Collapsed;
		this.MatrixView.Visibility = selected == 2 ? Visibility.Visible : Visibility.Collapsed;
		this.ThreeDView.Visibility = selected == 3 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void OnClearClicked(object? sender, RoutedEventArgs? e)
	{
		this.Skeleton?.ClearSelection();
	}

	private void OnSelectChildrenClicked(object sender, RoutedEventArgs e)
	{
		if (this.Skeleton == null)
			return;

		List<BoneVisual3d> bones = new List<BoneVisual3d>();
		foreach (BoneVisual3d bone in this.Skeleton.SelectedBones)
		{
			bone.GetChildren(ref bones);
		}

		this.Skeleton.Select(bones, SkeletonVisual3d.SelectMode.Add);
	}

	private void OnFlipClicked(object sender, RoutedEventArgs e)
	{
		if (this.Skeleton != null && !this.IsFlipping)
		{
			// if no bone selected, flip both lumbar and waist bones
			this.IsFlipping = true;
			if (this.Skeleton.CurrentBone == null)
			{
				BoneVisual3d? waistBone = this.Skeleton.GetBone("Waist");
				BoneVisual3d? lumbarBone = this.Skeleton.GetBone("SpineA");
				this.FlipBone(waistBone);
				this.FlipBone(lumbarBone);
				waistBone?.ReadTransform(true);
				lumbarBone?.ReadTransform(true);
			}
			else
			{
				// if targeted bone is a limb don't switch the respective left and right sides
				BoneVisual3d targetBone = this.Skeleton.CurrentBone;
				if (targetBone.BoneName.EndsWith("_l") || targetBone.BoneName.EndsWith("_r"))
				{
					this.FlipBone(targetBone, false);
				}
				else
				{
					this.FlipBone(targetBone);
				}

				targetBone.ReadTransform(true);
			}

			this.IsFlipping = false;
		}
	}

	private void OnParentClicked(object sender, RoutedEventArgs e)
	{
		if (this.Skeleton?.CurrentBone?.Parent == null)
			return;

		this.Skeleton.Select(this.Skeleton.CurrentBone.Parent);
	}

	private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left)
		{
			this.isLeftMouseButtonDownOnWindow = true;
			this.origMouseDownPoint = e.GetPosition(this.SelectionCanvas);
		}
	}

	private void OnCanvasMouseMove(object sender, MouseEventArgs e)
	{
		if (this.Skeleton == null)
			return;

		Point curMouseDownPoint = e.GetPosition(this.SelectionCanvas);

		if (this.isDragging)
		{
			e.Handled = true;

			this.DragSelectionBorder.Visibility = Visibility.Visible;

			double minx = Math.Min(curMouseDownPoint.X, this.origMouseDownPoint.X);
			double miny = Math.Min(curMouseDownPoint.Y, this.origMouseDownPoint.Y);
			double maxx = Math.Max(curMouseDownPoint.X, this.origMouseDownPoint.X);
			double maxy = Math.Max(curMouseDownPoint.Y, this.origMouseDownPoint.Y);

			minx = Math.Max(minx, 0);
			miny = Math.Max(miny, 0);
			maxx = Math.Min(maxx, this.SelectionCanvas.ActualWidth);
			maxy = Math.Min(maxy, this.SelectionCanvas.ActualHeight);

			Canvas.SetLeft(this.DragSelectionBorder, minx);
			Canvas.SetTop(this.DragSelectionBorder, miny);
			this.DragSelectionBorder.Width = maxx - minx;
			this.DragSelectionBorder.Height = maxy - miny;

			List<BoneView> bones = new List<BoneView>();

			foreach (BoneView bone in this.BoneViews)
			{
				if (bone.Bone == null)
					continue;

				if (!bone.IsDescendantOf(this.MouseCanvas))
					continue;

				if (!bone.IsVisible)
					continue;

				Point relativePoint = bone.TransformToAncestor(this.MouseCanvas).Transform(new Point(0, 0));
				if (relativePoint.X > minx && relativePoint.X < maxx && relativePoint.Y > miny && relativePoint.Y < maxy)
				{
					this.Skeleton.Hover(bone.Bone, true, false);
				}
				else
				{
					this.Skeleton.Hover(bone.Bone, false);
				}
			}

			this.Skeleton.NotifyHover();
		}
		else if (this.isLeftMouseButtonDownOnWindow)
		{
			System.Windows.Vector dragDelta = curMouseDownPoint - this.origMouseDownPoint;
			double dragDistance = Math.Abs(dragDelta.Length);
			if (dragDistance > DragThreshold)
			{
				this.isDragging = true;
				this.MouseCanvas.CaptureMouse();
				e.Handled = true;
			}
		}
	}

	private void OnCanvasMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (!this.isLeftMouseButtonDownOnWindow)
			return;

		this.isLeftMouseButtonDownOnWindow = false;
		if (this.isDragging)
		{
			this.isDragging = false;

			if (this.Skeleton != null)
			{
				double minx = Canvas.GetLeft(this.DragSelectionBorder);
				double miny = Canvas.GetTop(this.DragSelectionBorder);
				double maxx = minx + this.DragSelectionBorder.Width;
				double maxy = miny + this.DragSelectionBorder.Height;

				List<BoneView> toSelect = new List<BoneView>();

				foreach (BoneView bone in this.BoneViews)
				{
					if (bone.Bone == null)
						continue;

					if (!bone.IsVisible)
						continue;

					this.Skeleton.Hover(bone.Bone, false);

					Point relativePoint = bone.TransformToAncestor(this.MouseCanvas).Transform(new Point(0, 0));
					if (relativePoint.X > minx && relativePoint.X < maxx && relativePoint.Y > miny && relativePoint.Y < maxy)
					{
						toSelect.Add(bone);
					}
				}

				this.Skeleton.Select(toSelect);
			}

			this.DragSelectionBorder.Visibility = Visibility.Collapsed;
			e.Handled = true;
		}
		else
		{
			if (this.Skeleton != null && !this.Skeleton.HasHover)
			{
				this.Skeleton.Select(Enumerable.Empty<IBone>());
			}
		}

		this.MouseCanvas.ReleaseMouseCapture();
	}

	private void OnHistoryApplied()
	{
		this.Skeleton?.CurrentBone?.ReadTransform();
	}

	private async Task WriteSkeletonThread()
	{
		while (Application.Current != null && this.Skeleton != null)
		{
			await Dispatch.MainThread();

			if (this.Skeleton == null)
				return;

			this.Skeleton.WriteSkeleton();

			// up to 60 times a second
			await Task.Delay(16);
		}
	}
}
