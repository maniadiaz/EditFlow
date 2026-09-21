// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using EditFlow.Core.Projects;

namespace EditFlow.Engine.Tests.Projects;

public sealed class RecentProjectsStoreTests : IDisposable
{
    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("editflow-recent-");
    private readonly RecentProjectsStore _store;

    public RecentProjectsStoreTests() =>
        _store = new RecentProjectsStore(Path.Combine(_workspace.FullName, "data", "recent.json"), capacity: 3);

    public void Dispose() => _workspace.Delete(recursive: true);

    private string ProjectFile(string name)
    {
        var path = Path.Combine(_workspace.FullName, name + ".editflow");
        File.WriteAllText(path, "{}");
        return path;
    }

    private static RecentProject Entry(string path, int minutesAgo = 0) =>
        new(path, DateTime.UtcNow.AddMinutes(-minutesAgo), ClipCount: 2, TimeSpan.FromSeconds(30));

    [Fact]
    public void An_empty_store_returns_nothing() => Assert.Empty(_store.Load());

    [Fact]
    public void Recorded_projects_come_back_most_recent_first()
    {
        var a = ProjectFile("a");
        var b = ProjectFile("b");

        _store.Record(Entry(a));
        _store.Record(Entry(b));

        Assert.Equal([b, a], _store.Load().Select(p => p.FilePath));
    }

    [Fact]
    public void Recording_a_project_again_moves_it_to_the_front_without_duplicating()
    {
        var a = ProjectFile("a");
        var b = ProjectFile("b");

        _store.Record(Entry(a));
        _store.Record(Entry(b));
        _store.Record(Entry(a) with { ClipCount = 9 });

        var list = _store.Load();
        Assert.Equal([a, b], list.Select(p => p.FilePath));
        Assert.Equal(9, list[0].ClipCount);
    }

    [Fact]
    public void The_list_is_capped_dropping_the_oldest()
    {
        var files = Enumerable.Range(1, 5).Select(i => ProjectFile("p" + i)).ToArray();
        foreach (var file in files)
        {
            _store.Record(Entry(file));
        }

        Assert.Equal(files.Skip(2).Reverse(), _store.Load().Select(p => p.FilePath));
    }

    [Fact]
    public void Projects_whose_file_disappeared_are_left_out_and_forgotten()
    {
        var a = ProjectFile("a");
        var b = ProjectFile("b");
        _store.Record(Entry(a));
        _store.Record(Entry(b));

        File.Delete(a);

        Assert.Equal([b], _store.Load().Select(p => p.FilePath));
        Assert.Equal([b], _store.Load().Select(p => p.FilePath));
    }

    [Fact]
    public void Removing_forgets_the_entry_but_keeps_the_project_file()
    {
        var a = ProjectFile("a");
        _store.Record(Entry(a) with { ThumbnailPath = "portada.jpg" });

        var thumbnail = _store.Remove(a);

        Assert.Equal("portada.jpg", thumbnail);
        Assert.Empty(_store.Load());
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Removing_something_unknown_is_harmless() => Assert.Null(_store.Remove(ProjectFile("x")));

    [Fact]
    public void A_corrupt_file_yields_an_empty_list_instead_of_an_error()
    {
        var path = Path.Combine(_workspace.FullName, "corrupt.json");
        File.WriteAllText(path, "{ esto no es json");

        Assert.Empty(new RecentProjectsStore(path).Load());
    }

    [Fact]
    public void The_name_is_the_file_name_without_extension() =>
        Assert.Equal("boda", Entry(Path.Combine("x", "boda.editflow")).Name);
}
