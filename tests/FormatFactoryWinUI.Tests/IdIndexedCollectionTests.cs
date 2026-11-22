using FormatFactoryWinUI.Models;

namespace FormatFactoryWinUI.Tests;

public sealed class IdIndexedCollectionTests
{
    [Fact]
    public void IndexTracksInsertReplaceMoveRemoveClearAndUndoInsertion()
    {
        var first = new MediaTask();
        var second = new MediaTask();
        var tasks = new IdIndexedCollection<MediaTask>(task => task.Id) { first, second };
        tasks.CollectionChanged += (_, _) => Assert.All(tasks, task => Assert.Same(task, tasks.FindById(task.Id)));
        tasks.Move(0, 1);
        Assert.Same(first, tasks.FindById(first.Id));
        var replacement = new MediaTask { Id = first.Id };
        tasks[1] = replacement;
        Assert.False(tasks.ContainsReference(first));
        Assert.True(tasks.ContainsReference(replacement));
        tasks.Remove(replacement);
        Assert.Null(tasks.FindById(first.Id));
        tasks.Insert(0, first);
        Assert.Same(first, tasks.FindById(first.Id));
        tasks.Clear();
        Assert.Null(tasks.FindById(first.Id));
        Assert.Null(tasks.FindById(second.Id));
    }

    [Fact]
    public void DuplicateOrInvalidInsertionDoesNotCorruptIndex()
    {
        var task = new MediaTask();
        var tasks = new IdIndexedCollection<MediaTask>(item => item.Id) { task };
        Assert.Throws<ArgumentException>(() => tasks.Add(new MediaTask { Id = task.Id }));
        var other = new MediaTask();
        Assert.Throws<ArgumentOutOfRangeException>(() => tasks.Insert(5, other));
        Assert.Same(task, Assert.Single(tasks));
        Assert.Null(tasks.FindById(other.Id));
    }
}
