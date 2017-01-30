﻿#region License
/* 
 * Copyright (C) 1999-2017 John Källén.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2, or (at your option)
 * any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; see the file COPYING.  If not, write to
 * the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA.
 */
#endregion

using Reko.Core;
using Reko.Core.Lib;
using Reko.Core.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Reko.Scanning
{
    /// <summary>
    /// This class uses a provided interprocedural control flow graph to 
    /// detect the starts of procedures.
    /// </summary>
    /// <remarks>
    /// Inspired by "Compiler-Agnostic Function Detection in Binaries", by 
    /// Dennis Andriesse, Asia Slowinska, Herbert Bos.
    /// </remarks>
    public class ProcedureDetector
    {
        private Program program;
        private ScanResults sr;
        private DecompilerEventListener listener;
        private HashSet<Address> knownProcedures;
        private Dictionary<Address, RtlBlock> mpAddrToBlock;
        private object e;

        public ProcedureDetector(Program program, ScanResults sr, DecompilerEventListener listener)
        {
            this.program = program;
            this.sr = sr;
            this.listener = listener;
            this.knownProcedures = sr.KnownProcedures.Concat(sr.DirectlyCalledAddresses.Keys).ToHashSet();
            this.mpAddrToBlock = sr.ICFG.Nodes.ToDictionary(de => de.Address);
        }

        /// <summary>
        /// Master function to locate "Clusters" of RtlBlocks from the ICFG
        /// passed in the ScanResults.
        /// </summary>
        /// <returns></returns>
        public List<Cluster> DetectProcedures()
        {
            PreprocessIcfg();
            var clusters = FindClusters();
            return BuildProcedures(clusters);
        }

        private void PreprocessIcfg()
        {
            RemoveJumpsToKnownProcedures();
            ProcessIndirectJumps();
        }

        /// <summary>
        /// Remove any links between nodes where the destination is 
        /// a known call target.
        /// </summary>
        public void RemoveJumpsToKnownProcedures()
        {
            foreach (var calldest in this.knownProcedures)
            {
                if (listener.IsCanceled())
                    break;
                var node = mpAddrToBlock[calldest];
                var preds = sr.ICFG.Predecessors(node).ToList();
                foreach (var p in preds)
                {
                    sr.ICFG.RemoveEdge(p, node);
                }
            }
        }

        private void ProcessIndirectJumps()
        {
            //$TODO: need some form of backwalking here.
        }

        /// <summary>
        /// A cluster is a proto-procedure. It consists of a set of entries
        /// and a set of blocks. 
        /// </summary>
        public class Cluster
        {
            public SortedSet<RtlBlock> Blocks = new SortedSet<RtlBlock>(Cmp.Instance);
            public SortedSet<RtlBlock> Entries = new SortedSet<RtlBlock>(Cmp.Instance);

            public Cluster()
            {
                this.Entries = new SortedSet<RtlBlock>(Cmp.Instance);
                this.Blocks = new SortedSet<RtlBlock>(Cmp.Instance);
            }

            public Cluster(IEnumerable<RtlBlock> entries, IEnumerable<RtlBlock> blocks)
            {
                this.Entries = new SortedSet<RtlBlock>(entries, Cmp.Instance);
                this.Blocks = new SortedSet<RtlBlock>(blocks, Cmp.Instance);
            }

            private class Cmp : Comparer<RtlBlock>
            {
                public override int Compare(RtlBlock x, RtlBlock y)
                {
                    return x.Address.CompareTo(y.Address);
                }

                public static readonly Cmp Instance = new Cmp();
            }
        }

        /// <summary>
        /// Collects weakly connected components from the ICFG and gathers
        /// them into Clusters.
        /// </summary>
        /// <param name="sr"></param>
        /// <returns></returns>
        public List<Cluster> FindClusters()
        {
            var nodesLeft = new HashSet<RtlBlock>(sr.ICFG.Nodes);
            var clusters = new List<Cluster>();
            while (nodesLeft.Count > 0)
            {
                if (listener.IsCanceled())
                    break;
                var node = nodesLeft.First();
                var cluster = new Cluster();
                clusters.Add(cluster);

                BuildWCC(node, cluster, nodesLeft);
            }
            return clusters;
        }

        /// <summary>
        /// Build the weakly connected component for a cluster by following 
        /// both predecessors and successors in the graph. However, we never
        /// follow the predecessors of nodes that are marked directly called,
        /// and we never follow successors that are marked directly called
        /// (tail calls).
        /// </summary>
        /// <param name="node"></param>
        /// <param name="cluster"></param>
        /// <param name="nodesLeft"></param>
        private void BuildWCC(
            RtlBlock node,
            Cluster cluster,
            HashSet<RtlBlock> nodesLeft)
        {
            nodesLeft.Remove(node);
            cluster.Blocks.Add(node);

            foreach (var s in sr.ICFG.Successors(node))
            {
                if (nodesLeft.Contains(s))
                {
                    // Only add if successor is not CALLed.
                    if (!knownProcedures.Contains(s.Address))
                    {
                        BuildWCC(s, cluster, nodesLeft);
                    }
                }
            }
            if (!knownProcedures.Contains(node.Address))
            {
                // Only backtrack through predecessors if the node
                // is not CALLed.
                foreach (var p in sr.ICFG.Predecessors(node))
                {
                    if (nodesLeft.Contains(p))
                    {
                        BuildWCC(p, cluster, nodesLeft);
                    }
                }
            }
        }

        /// <summary>
        /// Given a set of clusters, finds all the entries for each cluster 
        /// and tries to partition each cluster into procedures with single
        /// entries and exits.
        /// </summary>
        /// <param name="sr"></param>
        /// <param name="clusters"></param>
        public List<Cluster> BuildProcedures(IEnumerable<Cluster> clusters)
        {
            var clustersOut = new List<Cluster>();
            foreach (var cluster in clusters)
            {
                if (listener.IsCanceled())
                    break;
                FindClusterEntries(cluster);
                clustersOut.AddRange(PostProcessCluster(cluster));
            }
            return clustersOut;
        }

        /// <summary>
        /// For a given cluster, find the probable entries.
        /// </summary>
        /// <param name="sr"></param>
        /// <param name="cluster"></param>
        public void FindClusterEntries(Cluster cluster)
        {
            var nopreds = new List<RtlBlock>();
            foreach (var block in cluster.Blocks)
            {
                if (knownProcedures.Contains(block.Address))
                {
                    cluster.Entries.Add(block);
                }
                if (sr.ICFG.Predecessors(block).Count == 0)
                {
                    nopreds.Add(block);
                }
            }

            // If one or more nodes were the destination of a direct call,
            // use those as entries.
            if (cluster.Entries.Count > 0)
                return;

            // Otherwise, if one or more nodes has zero predecessors, pick it.
            if (nopreds.Count > 0)
            {
                cluster.Entries.UnionWith(nopreds);
                return;
            }

            // If we can't find another possibility, return the node with the
            // lowest address.
            cluster.Entries.Add(cluster.Blocks.Min());
        }

        /// <summary>
        /// Processes a cluster into 1..n procedures.
        /// </summary>
        /// <param name="cluster"></param>
        /// <param name="entries"></param>
        /// <returns></returns>
        public List<Cluster> PostProcessCluster(Cluster cluster)
        {
            var entries = cluster.Entries;

            // Remove all nodes with no predecessors which haven't been marked as entries.
            var deadNodes = cluster.Blocks
                .Where(b => !entries.Contains(b) && sr.ICFG.Predecessors(b).Count == 0)
                .ToHashSet();
            cluster.Blocks.ExceptWith(deadNodes);

            // Join blocks which have a single successor / single predecessor
            // relationship.
            FuseBlocks(cluster);

            // If the cluster has more than one entry, we have to try to pick it apart.
            if (entries.Count > 1)
            {
                Debug.Print("Entries {0} share common code", string.Join(",", entries.Select(e => e.Name)));

                return PartitionIntoSubclusters(cluster);

            }
            else
            {
                return new List<Cluster> { cluster };
            }
        }

        /// <summary>
        /// Splits a multiple entry cluster into separate sub-clusters by 
        /// partitioning all the blocks into subsets where each subset is 
        /// dominated by one of the original entries. 
        /// </summary>
        /// <remarks>
        /// Many binaries contain cross-procedure jumps. If the target of 
        /// those jumps is a single block with no successors, it is very
        /// likely an instance of a "shared exit node" pattern that many
        /// compilers+linkers emit. We handle that case separately.
        /// </remarks>
        /// <param name="cluster"></param>
        /// <returns></returns>
        public List<Cluster> PartitionIntoSubclusters(Cluster cluster)
        {
            // Create a fake node that will serve as the parent of all the 
            // existing entries. That node will be used to compute all
            // immediate dominatores of all reachable blocks.
            var auxNode = new RtlBlock(null, "<root>");
            sr.ICFG.AddNode(auxNode);
            foreach (var entry in cluster.Entries)
            {
                sr.ICFG.AddEdge(auxNode, entry);
            }
            var idoms = LTDominatorGraph<RtlBlock>.Create(sr.ICFG, auxNode);

            // Find all nodes whose immediate dominator is "<root>". 
            // Those are the entries to new clusters and may contain blocks
            // that are shared between procedures in the source program.
            var newEntries = cluster.Blocks.Where(b => idoms[b] == auxNode).ToList();
            var dominatedEntries = newEntries.ToDictionary(k => k, v => new HashSet<RtlBlock> { v });

            // Partition the nodes in the cluster into categories depending on which
            // one of the newEntries they are dominated by.
            foreach (var b in cluster.Blocks)
            {
                if (dominatedEntries.ContainsKey(b))
                    continue; // already there.
                var n = b;
                for (;;)
                {
                    var i = idoms[n];
                    if (dominatedEntries.ContainsKey(i))
                    {
                        // If my idom is already in the set, add me too.
                        dominatedEntries[i].Add(b);
                        break;
                    }
                    n = i;
                }
            }

            // Now remove the fake node 
            sr.ICFG.RemoveNode(auxNode);

            // Handle the special case with new entries that weren't there before,
            // and only consist of a single block. Mark such nodes as "shared".
            // Later stages will copy these nodes into their respective procedures.
            foreach (var newEntry in dominatedEntries.Keys
                .Where(e => !cluster.Entries.Contains(e)).ToList())
            {
                if (sr.ICFG.Successors(newEntry).Count == 0)
                {
                    newEntry.IsSharedExitBlock = true;
                    dominatedEntries.Remove(newEntry);
                }
            }

            return dominatedEntries
                .OrderBy(e => e.Key.Address)
                .Select(e => new Cluster(new[] { e.Key }, e.Value))
                .ToList();
        }

        [Conditional("DEBUG")]
        private void DumpDomGraph(IEnumerable<RtlBlock> nodes, Dictionary<RtlBlock, RtlBlock> domGraph)
        {
            var q =
                from n in nodes
                join de in domGraph on n equals de.Value into des
                from de in des.DefaultIfEmpty()
                orderby n.Name, de.Key != null ? de.Key.Name : ""
                select new { Name = n.Name, Kid = de.Key != null ? de.Key.Name : "*" };
            foreach (var item in q)
            {
                Debug.Print("{0}: {1}", item.Name, item.Kid );
            }
        }

        private void FuseBlocks(Cluster cluster)
        {
            foreach (var block in Enumerable.Reverse(cluster.Blocks).ToList())
            {
                var succs = sr.ICFG.Successors(block);
                if (succs.Count == 1)
                {
                    var s = succs.First();
                    var preds = sr.ICFG.Predecessors(s);
                    if (preds.Count != 1 || preds.First() != block)
                        continue;

                    if (block.GetEndAddress() != s.Address)
                        continue;
                    var ss = sr.ICFG.Successors(s).ToList();
                    sr.ICFG.RemoveEdge(block, s);
                    block.Instructions.AddRange(s.Instructions);
                    sr.ICFG.RemoveNode(s);
                    foreach (var n in ss)
                    {
                        sr.ICFG.AddEdge(block, n);
                    }
                    Debug.Print("Fused {0} {1}", block.Address, s.Address);
                }
            }
        }
    }
}