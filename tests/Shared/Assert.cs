using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace RimWorldModMcp.Tests;

public static class Assert
{
    public static void That<TActual>(TActual actual, IResolveConstraint expression, string? message = null)
    {
        if (message != null)
            NUnit.Framework.Assert.That(actual, expression, (NUnitString)message);
        else
            NUnit.Framework.Assert.That(actual, expression);
    }

    public static void That<TActual>(Func<TActual> del, IResolveConstraint expr, string? message = null)
    {
        if (message != null)
            NUnit.Framework.Assert.That(del, expr, (NUnitString)message);
        else
            NUnit.Framework.Assert.That(del, expr);
    }

    public static void That(Action code, IResolveConstraint constraint, string? message = null)
    {
        if (message != null)
            NUnit.Framework.Assert.That(code, constraint, (NUnitString)message);
        else
            NUnit.Framework.Assert.That(code, constraint);
    }

    public static void That(bool condition, string? message = null)
    {
        if (message != null)
            NUnit.Framework.Assert.That(condition, (NUnitString)message);
        else
            NUnit.Framework.Assert.That(condition);
    }

    public static void Pass(string? message = null)
    {
        if (message != null)
            NUnit.Framework.Assert.Pass(message);
        else
            NUnit.Framework.Assert.Pass();
    }

    public static void Fail(string? message = null)
    {
        if (message != null)
            NUnit.Framework.Assert.Fail(message);
        else
            NUnit.Framework.Assert.Fail();
    }

    public static void Ignore(string? message = null)
    {
        if (message != null)
            NUnit.Framework.Assert.Ignore(message);
        else
            NUnit.Framework.Assert.Ignore();
    }

    public static void True(bool? condition, string? userMessage = null)
    {
        if (userMessage != null)
            NUnit.Framework.Assert.That(condition, Is.True, (NUnitString)userMessage);
        else
            NUnit.Framework.Assert.That(condition, Is.True);
    }

    public static void False(bool? condition, string? userMessage = null)
    {
        if (userMessage != null)
            NUnit.Framework.Assert.That(condition, Is.False, (NUnitString)userMessage);
        else
            NUnit.Framework.Assert.That(condition, Is.False);
    }

    public static void Null(object? @object)
        => NUnit.Framework.Assert.That(@object, Is.Null);

    public static void NotNull([NotNull] object? @object)
    {
        if (@object is null)
        {
            throw new AssertionException("Expected: not null\n  But was: null");
        }
    }

    public static void Equal<T>(T expected, T actual)
        => NUnit.Framework.Assert.That(actual, Is.EqualTo(expected));

    public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        => NUnit.Framework.Assert.That(actual, Is.EqualTo(expected));

    public static void Equal<T>(T expected, T actual, IEqualityComparer<T> comparer)
        => NUnit.Framework.Assert.That(actual, Is.EqualTo(expected).Using(comparer));

    public static void NotEqual<T>(T expected, T actual)
        => NUnit.Framework.Assert.That(actual, Is.Not.EqualTo(expected));

    public static void NotEqual<T>(T expected, T actual, IEqualityComparer<T> comparer)
        => NUnit.Framework.Assert.That(actual, Is.Not.EqualTo(expected).Using(comparer));

    public static void Empty(IEnumerable? collection)
        => NUnit.Framework.Assert.That(collection, Is.Empty);

    public static void NotEmpty(IEnumerable? collection)
        => NUnit.Framework.Assert.That(collection, Is.Not.Empty);

    public static void InRange<T>(T actual, T low, T high) where T : IComparable<T>
        => NUnit.Framework.Assert.That(actual, Is.InRange(low, high));

    public static T IsType<T>(object? @object, bool exactMatch = true)
    {
        if (exactMatch)
            NUnit.Framework.Assert.That(@object, Is.TypeOf<T>());
        else
            NUnit.Framework.Assert.That(@object, Is.InstanceOf<T>());
        return (T)@object!;
    }

    public static void Single(IEnumerable collection)
    {
        NUnit.Framework.Assert.That(collection, Is.Not.Null);
        var enumerator = collection.GetEnumerator();
        try
        {
            if (!enumerator.MoveNext())
                throw new AssertionException("Collection was expected to contain a single element, but was empty.");
            if (enumerator.MoveNext())
                throw new AssertionException("Collection was expected to contain a single element, but contained more than one.");
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    public static T Single<T>(IEnumerable<T> collection)
    {
        NUnit.Framework.Assert.That(collection, Is.Not.Null);
        using var enumerator = collection.GetEnumerator();
        if (!enumerator.MoveNext())
            throw new AssertionException("Collection was expected to contain a single element, but was empty.");
        var item = enumerator.Current;
        if (enumerator.MoveNext())
            throw new AssertionException("Collection was expected to contain a single element, but contained more than one.");
        return item;
    }

    public static T Single<T>(IEnumerable<T> collection, Func<T, bool> predicate)
    {
        NUnit.Framework.Assert.That(collection, Is.Not.Null);
        var matches = collection.Where(predicate).ToList();
        if (matches.Count == 0)
            throw new AssertionException("No matching element was found.");
        if (matches.Count > 1)
            throw new AssertionException($"Expected 1 matching element, but found {matches.Count}.");
        return matches[0];
    }

    public static void Contains(string expectedSubstring, string? actualString, StringComparison comparison = StringComparison.Ordinal)
    {
        NUnit.Framework.Assert.That(actualString, Is.Not.Null);
        if (actualString!.IndexOf(expectedSubstring, comparison) < 0)
        {
            throw new AssertionException($"Expected string to contain '{expectedSubstring}' with {comparison}, but was: '{actualString}'");
        }
    }

    public static void Contains<T>(T expected, IEnumerable<T> collection)
        => NUnit.Framework.Assert.That(collection, Does.Contain(expected));

    public static void Contains<T>(T expected, IEnumerable<T> collection, IEqualityComparer<T> comparer)
        => NUnit.Framework.Assert.That(collection, Does.Contain(expected).Using(comparer));

    public static void Contains<T>(IEnumerable<T> collection, Func<T, bool> filter)
    {
        NUnit.Framework.Assert.That(collection, Is.Not.Null);
        if (!collection.Any(filter))
            throw new AssertionException("Collection does not contain any matching element.");
    }

    public static void DoesNotContain<T>(IEnumerable<T> collection, Func<T, bool> filter)
    {
        NUnit.Framework.Assert.That(collection, Is.Not.Null);
        if (collection.Any(filter))
            throw new AssertionException("Collection contains matching element.");
    }

    public static void DoesNotContain(string expectedSubstring, string? actualString, StringComparison comparison = StringComparison.Ordinal)
    {
        if (actualString is not null && actualString.Contains(expectedSubstring, comparison))
        {
            throw new AssertionException($"Expected string not to contain '{expectedSubstring}' with {comparison}, but was: '{actualString}'");
        }
    }

    public static void DoesNotContain<T>(T expected, IEnumerable<T> collection)
        => NUnit.Framework.Assert.That(collection, Does.Not.Contain(expected));

    public static void DoesNotContain<T>(T expected, IEnumerable<T> collection, IEqualityComparer<T> comparer)
        => NUnit.Framework.Assert.That(collection, Does.Not.Contain(expected).Using(comparer));

    public static void StartsWith(string expectedStartString, string? actualString, StringComparison comparison = StringComparison.Ordinal)
    {
        NUnit.Framework.Assert.That(actualString, Is.Not.Null);
        if (!actualString!.StartsWith(expectedStartString, comparison))
        {
            throw new AssertionException($"Expected string to start with '{expectedStartString}', but was: '{actualString}'");
        }
    }

    public static void EndsWith(string expectedEndString, string? actualString, StringComparison comparison = StringComparison.Ordinal)
    {
        NUnit.Framework.Assert.That(actualString, Is.Not.Null);
        if (!actualString!.EndsWith(expectedEndString, comparison))
        {
            throw new AssertionException($"Expected string to end with '{expectedEndString}', but was: '{actualString}'");
        }
    }

    public static T Throws<T>(Action testCode) where T : Exception
        => NUnit.Framework.Assert.Throws<T>(() => testCode())!;

    public static T Throws<T>(Func<object?> testCode) where T : Exception
        => NUnit.Framework.Assert.Throws<T>(() => { testCode(); })!;

    public static async Task<T> ThrowsAsync<T>(Func<Task> testCode) where T : Exception
        => (await NUnit.Framework.Assert.ThrowsAsync<T>(() => testCode()))!;

    public static Exception ThrowsAny<T>(Action testCode) where T : Exception
        => NUnit.Framework.Assert.Catch<T>(() => testCode())!;

    public static void All<T>(IEnumerable<T> collection, Action<T> action)
    {
        NUnit.Framework.Assert.That(collection, Is.Not.Null);
        var list = collection.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            try
            {
                action(list[i]);
            }
            catch (Exception ex)
            {
                throw new AssertionException($"Assertion failed for element at index {i}: {ex.Message}", ex);
            }
        }
    }

    public static void Collection<T>(IEnumerable<T> collection, params Action<T>[] elementInspectors)
    {
        NUnit.Framework.Assert.That(collection, Is.Not.Null);
        var list = collection.ToList();
        Equal(elementInspectors.Length, list.Count);
        for (var i = 0; i < elementInspectors.Length; i++)
        {
            try
            {
                elementInspectors[i](list[i]);
            }
            catch (Exception ex)
            {
                throw new AssertionException($"Collection inspector at index {i} failed: {ex.Message}", ex);
            }
        }
    }
}
