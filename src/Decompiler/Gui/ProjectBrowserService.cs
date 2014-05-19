﻿#region License
/* 
 * Copyright (C) 1999-2014 John Källén.
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

using Decompiler.Core;
using Decompiler.Gui.Controls;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Decompiler.Gui
{
    /// <summary>
    /// Interactor class used to display the decompiler project as a tree view for user browsing.
    /// </summary>
    public class ProjectBrowserService : IProjectBrowserService, ITreeNodeDesignerHost
    {
        private ITreeView tree;
        private Dictionary<object, TreeNodeDesigner> mpitemToDesigner;

        public ProjectBrowserService(IServiceProvider services, ITreeView treeView)
        {
            this.Services = services;
            this.tree = treeView;
            this.mpitemToDesigner = new Dictionary<object, TreeNodeDesigner>();
            this.tree.AfterSelect += tree_AfterSelect;
        }

        public IServiceProvider Services { get; private set; }

        public void Load(Project project, Program prog)
        {
            tree.Nodes.Clear();
            this.mpitemToDesigner = new Dictionary<object, TreeNodeDesigner>();
            if (project == null)
            {
                tree.ShowRootLines = false;
                tree.ShowNodeToolTips = false;
                tree.Nodes.Clear();
                tree.Nodes.Add(tree.CreateNode("(No project loaded)"));
                return;
            }
            else
            {
                AddComponents(project.InputFiles);
                AddComponents(project.InputFiles[0], prog.ImageMap.Segments.Values);
                tree.ShowNodeToolTips = true;
            }
        }

        public void AddComponents(IEnumerable components)
        {
            List<ITreeNode> nodes = new List<ITreeNode>();
            foreach (object o in components)
            {
                var des = CreateDesigner(o);
                if (des != null)
                {
                    nodes.Add(des.TreeNode);
                }
            }
            tree.Nodes.AddRange(nodes);
        }

        private TreeNodeDesigner CreateDesigner(object o)
        {
            if (o == null)
                return null;
            TreeNodeDesigner des = o as TreeNodeDesigner;
            if (des == null)
            {
                var attr = o.GetType().GetCustomAttributes(typeof(DesignerAttribute), true);
                if (attr.Length > 0)
                {
                    var desType = Type.GetType(
                        ((DesignerAttribute) attr[0]).DesignerTypeName,
                        true);
                    des = (TreeNodeDesigner) Activator.CreateInstance(desType);
                }
                else
                {
                    des = new TreeNodeDesigner();
                }
            }
            mpitemToDesigner[o] = des;
            var node = CreateTreeNode(o, des);
            return des;
        }

        private ITreeNode CreateTreeNode(object o, TreeNodeDesigner des)
        {
            var node = tree.CreateNode();
            node.Tag = o;
            node.Expand();
            des.Services = Services;
            des.Host = this;
            des.TreeNode = node;
            des.Initialize(o);

            return node;
        }

        public void AddComponents(object parent, IEnumerable components)
        {
            TreeNodeDesigner parentDes = GetDesigner(parent);
            if (parentDes == null)
            {
                Debug.Print("No designer for parent object {0}", parent ?? "(null)");
                AddComponents(components);
                return;
            }
            var nodes = components
                .Cast<object>()
                .Select(o => CreateTreeNode(o, CreateDesigner(o)));
            parentDes.TreeNode.Nodes.AddRange(nodes);
        }

        public TreeNodeDesigner GetDesigner(object o)
        {
            if (o == null)
                return null;
            TreeNodeDesigner des;
            if (mpitemToDesigner.TryGetValue(o, out des))
                return des;
            else
                return null;
        }

        public void RemoveComponent(object component)
        {
            throw new NotImplementedException();
        }

        private void tree_AfterSelect(object sender, EventArgs e)
        {
            if (tree.SelectedItem == null)
                return;
            var des = GetDesigner(tree.SelectedItem);
            if (des == null)
                return;
            des.DoDefaultAction();
        }
    }
}