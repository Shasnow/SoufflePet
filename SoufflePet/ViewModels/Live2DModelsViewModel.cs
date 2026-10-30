// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.ViewModels;

/// <summary>
/// 模型管理窗口的视图模型：左侧模型列表 + 右侧配置编辑（名称 / 水印部件 Id）。
/// 列表直接就是 <see cref="SouffleSettings.Live2DModels"/>，导入即写回配置；
/// 切换当前模型只改下标，桌宠窗口监听到下标变化后会自行重载。
/// </summary>
public partial class Live2DModelsViewModel : ViewModelBase
{
    private readonly SouffleSettings _settings;
    private readonly ILive2DModelService _models;

    public Live2DModelsViewModel(SouffleSettings settings, ILive2DModelService models)
    {
        _settings = settings;
        _models = models;
        SelectedModel = Models.FirstOrDefault();

        _settings.PropertyChanged += OnSettingsChanged;
        Models.CollectionChanged += OnModelsChanged;
    }

    /// <summary>模型列表：直接绑定设置里的集合，增删即写回配置。</summary>
    public ObservableCollection<Live2DModelConfig> Models => _settings.Live2DModels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(RemovePrompt))]
    public partial Live2DModelConfig? SelectedModel { get; set; }

    public bool HasSelection => SelectedModel is not null;

    public string WindowTitle => $"{_settings.PetName} · 模型";

    /// <summary>标题栏下方的说明文案。</summary>
    public string ToolHint => "导入、切换或移除 Live2D 模型";

    /// <summary>导入/移除/保存结果提示（成功与失败都用这一行，空串表示不显示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultMessage))]
    public partial string ResultMessage { get; set; } = string.Empty;

    public bool HasResultMessage => ResultMessage.Length > 0;

    /// <summary>移除二次确认：整个模型目录会从磁盘删除，先确认再动手。</summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; set; }

    public string RemovePrompt => SelectedModel is { } model
        ? $"「{model.Name}」对应的模型目录会从磁盘删除：\n{Path.Combine(_models.ModelsRoot, model.FolderName)}"
        : string.Empty;

    /// <summary>列表上方的统计文案：模型数量与当前生效的那个。</summary>
    public string Status => Models.Count == 0
        ? "还没有模型，导入一个模型压缩包吧"
        : $"共 {Models.Count} 个模型 · 当前「{_settings.ActiveLive2DModel?.Name}」";

    /// <summary>从压缩包导入：解压、登记 exp3/motion3，成功后加入列表并设为当前模型。</summary>
    public void ImportFromArchive(string archivePath)
    {
        var result = _models.Import(archivePath);
        ResultMessage = result.Message;
        if (!result.Success || result.Model is not { } model) return;

        var existing = Models.FirstOrDefault(m =>
            string.Equals(m.Name, model.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            Models.Add(model);
            SelectedModel = model;
        }
        else
        {
            // 同名模型视为更新：只补检测到的水印 Id，不覆盖用户手填的值
            if (model.WatermarkPartId.Length > 0) existing.WatermarkPartId = model.WatermarkPartId;
            SelectedModel = existing;
        }

        _settings.Save();
        Activate();
    }

    /// <summary>把选中模型设为当前：只改下标，桌宠窗口监听到变化后重载。</summary>
    [RelayCommand]
    private void Activate()
    {
        if (SelectedModel is not { } model) return;

        var index = Models.IndexOf(model);
        if (index < 0) return;

        SetActiveModelIndex(index);
        ResultMessage = $"已切换到「{model.Name}」";
        OnPropertyChanged(nameof(Status));
    }

    /// <summary>保存右侧编辑的名称 / 水印 Id（这两个字段不触发 PropertyChanged，需显式落盘）。</summary>
    [RelayCommand]
    private void SaveEdit()
    {
        if (SelectedModel is not { } model) return;

        var result = _models.Update(model);
        ResultMessage = result.Message;
        if (!result.Success) return;

        _settings.Save();
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(RemovePrompt));
    }

    /// <summary>点击移除：先弹二次确认，确认后才真正删盘。</summary>
    [RelayCommand]
    private void RequestRemove()
    {
        if (SelectedModel is null) return;
        ResultMessage = string.Empty;
        OnPropertyChanged(nameof(RemovePrompt));
        IsConfirmingRemove = true;
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    /// <summary>确认移除：删目录、从列表移除，被删的若是当前模型则切到相邻的一个。</summary>
    [RelayCommand]
    private void ConfirmRemove()
    {
        IsConfirmingRemove = false;
        if (SelectedModel is not { } model) return;

        var removedIndex = Models.IndexOf(model);
        var previousIndex = _settings.ActiveLive2DModelIndex;
        var wasActive = removedIndex == previousIndex;

        var result = _models.Remove(model);
        ResultMessage = result.Message;
        if (!result.Success) return;

        Models.Remove(model);
        SelectedModel = Models.FirstOrDefault();

        if (wasActive)
            SetActiveModelIndex(Math.Clamp(removedIndex, 0, Math.Max(0, Models.Count - 1)));

        _settings.Save();
        OnPropertyChanged(nameof(Status));
    }

    /// <summary>
    /// 写当前模型下标。下标没变时（例如移除的正好是末尾那个，夹紧后仍是原值）显式补一次变更通知：
    /// 桌宠窗口只监听下标变化，没有通知就不会重载——这样既保证触发，又只有一次落盘、一次重载。
    /// </summary>
    private void SetActiveModelIndex(int index)
    {
        if (_settings.ActiveLive2DModelIndex == index)
        {
            _settings.NotifyActiveLive2DModelIndexChanged();
            return;
        }

        _settings.ActiveLive2DModelIndex = index;
    }

    /// <summary>在资源管理器中打开模型根目录；不存在时先创建。</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_models.ModelsRoot);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_models.ModelsRoot}\"")
                {
                    UseShellExecute = true
                });
        }
        catch (Exception)
        {
            ResultMessage = "打开模型目录失败";
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SouffleSettings.ActiveLive2DModelIndex))
            OnPropertyChanged(nameof(Status));
    }

    private void OnModelsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(Status));
}