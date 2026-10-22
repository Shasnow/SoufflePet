// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoufflePet.Models;

namespace SoufflePet.ViewModels;

/// <summary>提问面板里的一个选项：预设选项或固定的「自由输入」项。</summary>
public sealed partial class AskOptionViewModel : ObservableObject
{
    public AskOptionViewModel(string text, string groupName, bool isCustom, string? description = null)
    {
        Text = text;
        GroupName = groupName;
        IsCustom = isCustom;
        Description = description;
    }

    public string Text { get; }

    /// <summary>可选的补充说明，界面上显示为选项下方的小字。</summary>
    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>同一问题的单选项共用一个分组名。</summary>
    public string GroupName { get; }

    /// <summary>自由输入项：选中后展开文本框。</summary>
    public bool IsCustom { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>提问面板里的一个问题：问题正文 + 预设选项 + 固定的自由输入项。</summary>
public sealed partial class AskQuestionViewModel : ObservableObject
{
    /// <summary>自由输入项的文案（固定存在，保证用户总能自己写答案）。</summary>
    private const string CustomOptionText = "其他（自由输入）";

    public AskQuestionViewModel(UserQuestion question, int index)
    {
        Text = question.Text;
        var groupName = $"ask-question-{index}";
        Options = [.. question.Options.Select(option =>
            new AskOptionViewModel(option.Text, groupName, isCustom: false, option.Description))];
        Custom = new AskOptionViewModel(CustomOptionText, groupName, isCustom: true);
        Options.Add(Custom);

        foreach (var option in Options)
            option.PropertyChanged += OnOptionPropertyChanged;
    }

    public string Text { get; }

    public ObservableCollection<AskOptionViewModel> Options { get; }

    /// <summary>自由输入项（<see cref="Options" /> 的最后一项）。</summary>
    public AskOptionViewModel Custom { get; }

    /// <summary>自由输入项的文本框是否展开。</summary>
    public bool IsCustomSelected => Custom.IsSelected;

    /// <summary>自由输入的内容；一开始输入就自动选中自由输入项。</summary>
    [ObservableProperty]
    public partial string CustomText { get; set; } = string.Empty;

    /// <summary>当前答案：选中预设项取其文本，选中自由输入取输入框内容。</summary>
    public string Answer =>
        Custom.IsSelected
            ? CustomText.Trim()
            : Options.FirstOrDefault(option => option.IsSelected)?.Text ?? string.Empty;

    public bool HasAnswer => Answer.Length > 0;

    /// <summary>答案变化时触发，供面板刷新「提交」按钮的可用状态。</summary>
    public event Action? AnswerChanged;

    /// <summary>选中某个预设选项时触发（自由输入不算），面板据此自动翻到下一题。</summary>
    public event Action? PresetSelected;

    partial void OnCustomTextChanged(string value)
    {
        // 开始输入就切到自由输入项，避免预设项与手写内容同时存在时答案不明确
        if (value.Length > 0 && !Custom.IsSelected) Custom.IsSelected = true;
        RaiseAnswerChanged();
    }

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AskOptionViewModel.IsSelected)) return;

        if (ReferenceEquals(sender, Custom)) OnPropertyChanged(nameof(IsCustomSelected));
        if (sender is AskOptionViewModel { IsSelected: true, IsCustom: false }) PresetSelected?.Invoke();
        RaiseAnswerChanged();
    }

    private void RaiseAnswerChanged()
    {
        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(HasAnswer));
        AnswerChanged?.Invoke();
    }
}

/// <summary>
/// 提问面板：问题分页展示，一页一个；选中预设选项后自动翻到下一题，
/// 所有问题都回答后才能提交；提交或取消时调用底层请求，工具执行侧据此继续或结束。
/// </summary>
public sealed partial class AskUserViewModel : ObservableObject
{
    private readonly UserQuestionRequest _request;
    private readonly List<AskQuestionViewModel> _questions;

    public AskUserViewModel(UserQuestionRequest request)
    {
        _request = request;
        _questions = [.. request.Questions.Select((question, index) => new AskQuestionViewModel(question, index))];
        foreach (var question in _questions)
        {
            question.AnswerChanged += OnAnswerChanged;
            question.PresetSelected += () => AdvanceFrom(question);
        }
    }

    public IReadOnlyList<AskQuestionViewModel> Questions => _questions;

    /// <summary>当前展示的问题序号（从 0 开始）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentQuestion))]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial int CurrentIndex { get; set; }

    public AskQuestionViewModel CurrentQuestion => _questions[CurrentIndex];

    /// <summary>右上角的页码：当前问题 / 总问题。</summary>
    public string PageText => $"{CurrentIndex + 1} / {_questions.Count}";

    private bool CanGoPrevious => CurrentIndex > 0;

    private bool CanGoNext => CurrentIndex < _questions.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void Previous() => CurrentIndex--;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => CurrentIndex++;

    /// <summary>某一题选了预设项后自动翻到下一题（最后一题保持不动）。</summary>
    private void AdvanceFrom(AskQuestionViewModel question)
    {
        if (_questions.IndexOf(question) == CurrentIndex && CanGoNext) CurrentIndex++;
    }

    private void OnAnswerChanged() => SubmitCommand.NotifyCanExecuteChanged();

    /// <summary>所有问题都有答案时才允许提交。</summary>
    private bool CanSubmit => _questions.All(question => question.HasAnswer);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit() =>
        _request.Submit([.. _questions.Select(question => new UserAnswer(question.Text, question.Answer))]);

    [RelayCommand]
    private void Cancel() => _request.Cancel();
}
