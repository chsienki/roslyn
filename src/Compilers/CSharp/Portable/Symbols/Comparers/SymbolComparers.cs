using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    public sealed class NullabilityIgnoringComparer : IEqualityComparer<ISymbol>
    {
        private NullabilityIgnoringComparer() { }

        public static NullabilityIgnoringComparer Instance = new NullabilityIgnoringComparer();

        public bool Equals(ISymbol x, ISymbol y)
        {
            if (x is TypeSymbol tx && y is TypeSymbol ty)
            {
                // compare somehow
                return tx.Equals(ty, TypeCompareKind.AllNullableIgnoreOptions);
            }
            else
            {
                return x.Equals(y);
            }
        }

        public int GetHashCode(ISymbol obj)
        {
            return obj.GetHashCode();
        }
    }
}
