using AtFutureMe.Models;

namespace AtFutureMe.Services;

public static class CategoryTree
{
    public static string? ParentKey(Category category) =>
        string.IsNullOrWhiteSpace(category.ParentId) ? null : category.ParentId;

    public static Category? Parent(IEnumerable<Category> all, Category category)
    {
        var key = ParentKey(category);
        return key == null ? null : all.FirstOrDefault(c => c.Id == key);
    }

    public static IEnumerable<Category> InOrder(IReadOnlyList<Category> all)
    {
        var seen = new HashSet<string>();
        foreach (var root in ChildrenOf(all, null))
            foreach (var item in Walk(all, root, seen))
                yield return item;

        foreach (var leftover in all.Where(c => seen.Add(c.Id)))
            yield return leftover;
    }

    public static IEnumerable<Category> ChildrenOf(IEnumerable<Category> all, string? parentId) =>
        all.Where(c => ParentKey(c) == parentId);

    public static bool IsDescendant(IReadOnlyList<Category> all, Category ancestor, Category node)
    {
        var current = Parent(all, node);
        var guard = 0;
        while (current != null && guard++ < 64)
        {
            if (current.Id == ancestor.Id)
                return true;
            current = Parent(all, current);
        }

        return false;
    }

    public static IEnumerable<Category> Descendants(IReadOnlyList<Category> all, Category root) =>
        InOrder(all).Where(c => IsDescendant(all, root, c));

    public static string Path(IReadOnlyList<Category> all, Category category)
    {
        var parts = new List<string>();
        var current = category;
        var guard = 0;
        while (current != null && guard++ < 64)
        {
            parts.Add(current.Name);
            current = Parent(all, current);
        }

        parts.Reverse();
        return string.Join(" \\ ", parts);
    }

    public static void RefreshLabels(IReadOnlyList<Category> all)
    {
        Sanitize(all);
        foreach (var category in InOrder(all))
        {
            var depth = 0;
            var current = Parent(all, category);
            while (current != null && depth < 16)
            {
                depth++;
                current = Parent(all, current);
            }

            category.TreeLabel = new string(' ', depth * 4) + category.Name;
            category.PathLabel = Path(all, category);
        }
    }

    public static void Sanitize(IReadOnlyList<Category> all)
    {
        var ids = all.Select(c => c.Id).ToHashSet();
        foreach (var category in all)
        {
            var key = ParentKey(category);
            if (key == null || !ids.Contains(key) || key == category.Id)
                category.ParentId = null;
        }

        foreach (var category in all)
        {
            if (IsDescendant(all, category, category))
                category.ParentId = null;
        }
    }

    public static void MoveSibling(List<Category> all, Category item, int delta)
    {
        var parent = ParentKey(item);
        var siblings = all.Where(c => ParentKey(c) == parent).ToList();
        var index = siblings.FindIndex(c => c.Id == item.Id);
        var next = index + delta;
        if (index < 0 || next < 0 || next >= siblings.Count)
            return;

        var ia = all.IndexOf(siblings[index]);
        var ib = all.IndexOf(siblings[next]);
        if (ia < 0 || ib < 0)
            return;
        (all[ia], all[ib]) = (all[ib], all[ia]);
        Rebuild(all);
    }

    public static void PlaceInside(List<Category> all, Category item, Category parent)
    {
        if (item.Id == parent.Id || IsDescendant(all, item, parent))
            return;
        item.ParentId = parent.Id;
        Rebuild(all);
    }

    public static void PlaceRoot(List<Category> all, Category item)
    {
        item.ParentId = null;
        Rebuild(all);
    }

    public static void PlaceBeside(List<Category> all, Category item, Category target, bool after)
    {
        if (item.Id == target.Id || IsDescendant(all, item, target))
            return;
        item.ParentId = ParentKey(target);
        var subtree = all.Where(c => c.Id == item.Id || IsDescendant(all, item, c)).ToList();
        foreach (var node in subtree)
            all.Remove(node);
        var targetIndex = all.FindIndex(c => c.Id == target.Id);
        if (targetIndex < 0)
        {
            all.AddRange(subtree);
            Rebuild(all);
            return;
        }

        if (after)
        {
            var last = targetIndex;
            for (var i = targetIndex + 1; i < all.Count; i++)
            {
                if (IsDescendant(all, target, all[i]))
                    last = i;
                else
                    break;
            }

            all.InsertRange(last + 1, subtree);
        }
        else
        {
            all.InsertRange(targetIndex, subtree);
        }

        Rebuild(all);
    }

    public static void Rebuild(List<Category> all)
    {
        var ordered = InOrder(all).ToList();
        all.Clear();
        all.AddRange(ordered);
    }

    static IEnumerable<Category> Walk(IReadOnlyList<Category> all, Category node, HashSet<string> seen)
    {
        if (!seen.Add(node.Id))
            yield break;
        yield return node;
        foreach (var child in ChildrenOf(all, node.Id))
            foreach (var item in Walk(all, child, seen))
                yield return item;
    }
}
