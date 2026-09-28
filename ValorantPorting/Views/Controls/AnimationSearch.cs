using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ValorantPorting.Services.Endpoints;

namespace ValorantPorting.Views.Controls;

// Ranked animation search. Every search word must match (the readable title, Riot's file name or its folder), and the
// closer an animation's title is to what was typed, the higher it ranks: "run forward" puts "Run forward" first.
// Words also match Riot's internal names ("forward" -> RunN, "vandal" -> AK, "upper body" -> _UB, "jett" -> Wushu)
// and small typos ("vandl").
public sealed class AnimationSearch
{
    private readonly string phrase;
    private readonly List<string[]> words; // each typed word with the names it may also stand for

    private AnimationSearch(string phrase, List<string[]> words)
    {
        this.phrase = phrase;
        this.words = words;
    }

    // Riot's direction letters and body halves in file names ("RunN", "Equip_UB")
    private static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["forward"] = ["n", "fwd"], ["forwards"] = ["n", "fwd"], ["front"] = ["n", "fwd"],
        ["back"] = ["s", "bwd"], ["backward"] = ["s", "bwd"], ["backwards"] = ["s", "bwd"],
        ["left"] = ["w"], ["right"] = ["e"],
        ["upper"] = ["ub"], ["lower"] = ["lb"], ["legs"] = ["lb", "lower"],
        ["1st"] = ["fp", "first"], ["first"] = ["fp", "1st"], ["3rd"] = ["tp", "third"], ["third"] = ["tp", "3rd"],
        ["ult"] = ["x"], ["ultimate"] = ["x"], ["signature"] = ["e"], ["ability"] = ["q", "e", "c"],
        ["jog"] = ["run"], ["sprinting"] = ["sprint"], ["running"] = ["run"], ["walking"] = ["walk"]
    };

    private static readonly HashSet<string> Ignored = ["the", "a", "an", "animation", "animations", "anim", "of", "for"];

    public static AnimationSearch? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var phrase = Normalize(text);
        var typed = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Ignored.Contains(w)).ToList();
        if (typed.Count == 0) return null;

        var internalNames = InternalNames();
        var words = typed.Select(word =>
        {
            var options = new List<string> { word };
            if (Synonyms.TryGetValue(word, out var synonyms)) options.AddRange(synonyms);
            if (internalNames.TryGetValue(word, out var names)) options.AddRange(names);
            return options.Distinct().ToArray();
        }).ToList();
        return new AnimationSearch(phrase, words);
    }

    // Higher is better; null = no match.
    public double? Score(AnimationItem item)
    {
        var title = item.SearchTitle;
        var typos = 0;
        var titleOnly = true;
        foreach (var options in words)
        {
            if (options.Any(o => ContainsWord(title, item.TitleWords, o))) continue;
            titleOnly = false;
            if (options.Any(o => ContainsWord(item.SearchText, item.NameWords, o))) continue;
            if (options[0].Length >= 4 && item.AllWords.Any(w => IsTypo(options[0], w)))
            {
                typos++;
                continue;
            }

            return null;
        }

        double score = 0;
        if (title == phrase) score += 1000;
        else if (title.StartsWith(phrase)) score += 600;
        else if (title.Contains(phrase)) score += 400;
        else if (titleOnly) score += 200;
        if (item.IsShared) score += 60; // the common version before each agent's variant
        score -= typos * 150;
        score -= title.Length * 0.5; // shorter, more general names first
        return score;
    }

    // Short options ("n", "ub") only match whole words of the name; longer ones anywhere.
    private static bool ContainsWord(string text, HashSet<string> wordsOfText, string option) =>
        option.Length <= 2 ? wordsOfText.Contains(option) : text.Contains(option);

    private static bool IsTypo(string typed, string word)
    {
        if (Math.Abs(typed.Length - word.Length) > 1 || word.Length < 4) return false;
        // one letter wrong, missing or extra
        int i = 0, j = 0, edits = 0;
        while (i < typed.Length && j < word.Length)
        {
            if (typed[i] == word[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (typed.Length > word.Length) i++;
            else if (typed.Length < word.Length) j++;
            else { i++; j++; }
        }

        return edits + (typed.Length - i) + (word.Length - j) <= 1;
    }

    public static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    // Lower-case display name -> Riot's internal names ("vandal" -> "ak", "jett" -> "wushu")
    private static Dictionary<string, List<string>> internalNamesCache = new();
    private static int cachedCount = -1;

    private static Dictionary<string, List<string>> InternalNames()
    {
        var count = ValorantNames.Guns.Count + ValorantNames.Agents.Count;
        if (count == cachedCount) return internalNamesCache;
        var map = new Dictionary<string, List<string>>();
        void Add(string display, string internalName)
        {
            var key = Normalize(display).Replace(" ", "");
            if (key.Length == 0) return;
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(internalName.ToLowerInvariant());
        }

        foreach (var (folder, gun) in ValorantNames.Guns) Add(gun, folder);
        foreach (var (codename, agent) in ValorantNames.Agents) Add(agent.Name, codename);
        internalNamesCache = map;
        cachedCount = count;
        return map;
    }
}
