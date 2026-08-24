using System.Collections.Generic;
using System.Linq;

namespace MediaFlux.Services
{
    internal enum EncodingScopeChoice
    {
        EntireQueue,
        Selected,
        Cancel
    }

    internal sealed class EncodingScopeSummary<T> where T : class
    {
        public EncodingScopeSummary(IReadOnlyList<T> eligibleJobs, IReadOnlyList<T> selectedJobs)
        {
            EligibleJobs = eligibleJobs;
            SelectedJobs = selectedJobs;
        }

        public IReadOnlyList<T> EligibleJobs { get; }
        public IReadOnlyList<T> SelectedJobs { get; }
        public bool RequiresChoice => SelectedJobs.Count > 0 &&
                                    SelectedJobs.Count < EligibleJobs.Count;

        public IReadOnlyList<T>? Resolve(EncodingScopeChoice choice)
        {
            if (choice == EncodingScopeChoice.Cancel)
                return null;

            return choice == EncodingScopeChoice.Selected
                ? SelectedJobs
                : EligibleJobs;
        }
    }

    internal static class EncodingScopeResolver
    {
        public static EncodingScopeSummary<T> Analyze<T>(
            IEnumerable<T> eligibleJobs,
            IEnumerable<T> selectedJobs) where T : class
        {
            var eligible = eligibleJobs.ToList();
            var eligibleSet = eligible.ToHashSet();
            var selected = selectedJobs
                .Where(eligibleSet.Contains)
                .Distinct()
                .ToList();

            return new EncodingScopeSummary<T>(eligible, selected);
        }
    }
}
