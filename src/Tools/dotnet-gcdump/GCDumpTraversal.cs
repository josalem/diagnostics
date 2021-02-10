using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Graphs;

namespace Microsoft.Diagnostics.Tools.GCDump
{
    public class GCDumpTypeItem
    {
        public string TypeName;
        public string ModuleName;
        public long SizeNum;
        public long IncludedSizeNum;
        public long CountNum ;
        public NodeTypeIndex TypeIndex;
        public Dictionary<NodeTypeIndex, long> Parents;
        public Dictionary<NodeTypeIndex, long> Children;
    }

    public static class GraphExtensions
    {
        public static void DepthFirstVisit(this MemoryGraph graph, Action<Node> visitor)
        {
            var visited = new bool[(int)graph.NodeIndexLimit];
            var nodeStorage = graph.AllocNodeStorage();
            var rootIndex = graph.RootIndex;
            void DepthFirstVisitInternal(MemoryGraph g, NodeIndex w)
            {
                visited[(int)w] = true;
                var node = g.GetNode(w, graph.AllocNodeStorage());
                var i = 0;
                for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
                {
                    if (!visited[(int)child])
                        DepthFirstVisitInternal(g, child);
                    i++;
                }
                Console.WriteLine($"visited {i} children");
                // node = g.GetNode(w, nodeStorage);
                visitor(node);
            }
            DepthFirstVisitInternal(graph, rootIndex);
        }
    }

    public class GraphReader
    {
        private MemoryGraph MemoryGraph;
        private GCDumpTypeItem[] AllTypeItems;

        private GCDumpTypeItem GetTypeItemOfIndex(NodeTypeIndex index)
        {
            for (int i=0;i< AllTypeItems.Length;i++)
            {
                if (AllTypeItems[i].TypeIndex == index)
                    return AllTypeItems[i];
            }
            return new GCDumpTypeItem(); ;
        }
        private void SetTypeItemOfIndex(GCDumpTypeItem item)
        {
            for (int i = 0; i < AllTypeItems.Length; i++)
            {
                if (AllTypeItems[i] != null && AllTypeItems[i].TypeIndex == item.TypeIndex)
                {
                    AllTypeItems[i] = item;
                }
            }
        }

        private List<NodeIndex> GetChildrenOfNode(Graph graph, NodeIndex nodeIndex)
        {
            var node = graph.GetNode(nodeIndex, graph.AllocNodeStorage());
            var ret = new List<NodeIndex>();
            for (var childIndex = node.GetFirstChildIndex(); childIndex != NodeIndex.Invalid; childIndex = node.GetNextChildIndex())
            {
                ret.Add(childIndex);
            }
            return ret;
        }
        private void GetAllTypeItems()
        {
            AllTypeItems = new GCDumpTypeItem[MemoryGraph.m_types.Count];
            int idx = 0;
            var histogramByType = MemoryGraph.GetHistogramByType();

            string aaa = "";


            for (var index = 0; index < MemoryGraph.m_types.Count; index++)
            {
                var type = MemoryGraph.m_types[index];
                if (string.IsNullOrEmpty(type.Name))
                    continue;

                var sizeAndCount = histogramByType.FirstOrDefault(c => (int)c.TypeIdx == index);

                if (sizeAndCount == null ||
                    sizeAndCount.Count == 0 ||
                    type.Size == -1)
                {
                    continue;
                }

                if (!(!String.IsNullOrEmpty(type.Name) && Char.IsLetter(type.Name[0])))
                {
                    sizeAndCount.Size = 0;
                    type.Size = 0;
                    aaa += "------------------------------------------------------------------\n";
                    aaa += type.ModuleName+ "\n";
                    aaa += type.Name + "\n";
                    aaa += type.Size + "\n";
                    aaa += sizeAndCount.Size + "\n";
                    aaa += sizeAndCount.Count + "\n";  
                }

                GCDumpTypeItem item = new GCDumpTypeItem
                {
                    TypeName = type.Name,
                    ModuleName = type.ModuleName,
                    SizeNum = type.Size,
                    IncludedSizeNum = 0,
                    CountNum = sizeAndCount.Count,
                    TypeIndex = (NodeTypeIndex)index,
                    Parents = new Dictionary<NodeTypeIndex, long>(),
                    Children = new Dictionary<NodeTypeIndex, long>()
                };
                AllTypeItems[idx++] = item;
            }
            File.WriteAllText("aaa.txt", aaa);
        }
        private void GetAllParentsAndChildrenOfAllType()
        {
            // Check all node's children.
            for (NodeIndex nodeIdx = 0; nodeIdx < MemoryGraph.NodeIndexLimit; nodeIdx = nodeIdx + 1)
            {
                // Get node and nodeitem.
                var node = MemoryGraph.GetNode(nodeIdx, MemoryGraph.AllocNodeStorage());
                GCDumpTypeItem nodeTypeItem = GetTypeItemOfIndex(node.TypeIndex);

                foreach (NodeIndex chIdx in GetChildrenOfNode(MemoryGraph, nodeIdx))
                {
                    // Get child node and childnodeitem.
                    var chNode = MemoryGraph.GetNode(chIdx, MemoryGraph.AllocNodeStorage());
                    var chNodeTypeItem = GetTypeItemOfIndex(chNode.TypeIndex);

                    if ((chNode.TypeIndex == node.TypeIndex))
                    {
                        continue;
                    }

                    // Add parent node index to chNodeTypeItem.Parents (Dictionary)
                    if (chNodeTypeItem.Parents.ContainsKey(node.TypeIndex))
                    {
                        chNodeTypeItem.Parents[node.TypeIndex]++;
                    }
                    else
                    {
                        chNodeTypeItem.Parents.Add(node.TypeIndex, 1);
                    }

                    // Add child node index to nodeTypeItem.Children (Dictionary)
                    if (nodeTypeItem.Children.ContainsKey(chNode.TypeIndex))
                    {
                        nodeTypeItem.Children[chNode.TypeIndex]++;
                    }
                    else
                    {
                        nodeTypeItem.Children.Add(chNode.TypeIndex, 1);
                    }
                }
            }
        }

        private bool[] isVisited;
        private void CalculateAllIncludedSize()
        {
            isVisited = Enumerable.Repeat<bool>(false, AllTypeItems.Length).ToArray<bool>();

            for (int i = 0; i < AllTypeItems.Length; i++)
            {
                if(AllTypeItems[i] == null)
                {
                    break;
                }
                CalculateIncludedSize(AllTypeItems[i].TypeIndex);
            }
            //RefreshRealTypeItems();
        }
        private void CalculateIncludedSize(NodeTypeIndex index)
        {
            if (isVisited[(int)index])
            {
                return;
            }

            isVisited[(long)index] = true;

            GCDumpTypeItem item = GetTypeItemOfIndex(index);

            item.IncludedSizeNum = item.SizeNum;

            foreach (KeyValuePair<NodeTypeIndex, long> child in item.Children)
            {
                if(child.Key == index)
                {
                    continue;
                }
                CalculateIncludedSize(child.Key);
                GCDumpTypeItem citem = GetTypeItemOfIndex(child.Key);
                item.IncludedSizeNum += citem.IncludedSizeNum * child.Value;
            }
            SetTypeItemOfIndex(item);
        }

        public void Initialize(MemoryGraph graph)
        {
            MemoryGraph = graph;
            GetAllTypeItems();
            GetAllParentsAndChildrenOfAllType();
            CalculateAllIncludedSize();
        }
    }
}