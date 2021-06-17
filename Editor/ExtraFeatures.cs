using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using UnityEngine.UIElements;
using UnityEditor.Experimental.GraphView;
using UnityEditor.ShortcutManagement;

/*
Author : Cyanilux (https://twitter.com/Cyanilux)
Github Repo : https://github.com/Cyanilux/ShaderGraphVariables

Extra Features :
	- Colour of groups can be changed
		- Right-click group the group name and select Edit Group Colour
		- A colour picker should open, allowing you to choose a colour (including alpha)
		- Behind the scenes this creates a hidden Color node in the group. It can only be moved with
			the group so try to move the group as a whole rather than nodes inside it.
	- "S" key will swap the first two ports (usually A and B) on all currently selected nodes
		- Enabled for nodes : Add, Subtract, Multiply, Divide, Maximum, Minimum, Lerp, Inverse Lerp, Step and Smoothstep
		- Note : It needs at least one connection to swap. If both ports are empty, it won't swap the values as it doesn't 
			update properly and I couldn't find a way to force-update it.
	- Support for binding 10 hotkeys for quickly add specific nodes to the graph (at mouse position)
		- Defaults are :
			- 1 : Add
			- 2 : Subtract
			- 3 : Multiply
			- 4 : Lerp
			- 5 : Split
			- 6 : One Minus
			- 7 : Negate
			- 8 : Absolute
			- 9 : Step
			- 0 : Smoothstep
		- To change nodes : Tools > SGVariablesExtraFeatures > Rebind Node Bindings
		- To edit keybindings : Edit > Shortcuts (search for SGVariables)
			- Note, try to avoid conflicts with SG's hotkeys (mainly A, F and O) as those can't be rebound
			- https://www.cyanilux.com/tutorials/intro-to-shader-graph/#shortcuts

*/

using static Cyan.SGVariables;

namespace Cyan {

	public class ExtraFeatures {

		#region Extra Features (Swap Command)
		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Swap Ports On Selected Nodes _s")]
		private static void SwapPortsCommand() {
			if (graphView == null) return;
			if (debugMessages) Debug.Log("Swap Ports");

			List<ISelectable> selected = graphView.selection;
			foreach (ISelectable s in selected) {
				if (s is Node) {
					Node node = (Node)s;
					var materialNode = NodeToSGMaterialNode(node);
					string type = materialNode.GetType().ToString();
					type = type.Substring(type.LastIndexOf('.') + 1);

					if (type == "AddNode" || type == "SubtractNode" ||
						type == "MultiplyNode" || type == "DivideNode" ||
						type == "MaximumNode" || type == "MinimumNode" ||
						type == "LerpNode" || type == "InverseLerpNode" ||
						type == "StepNode" || type == "SmoothstepNode") {
						var inputPorts = GetInputPorts(node);
						Port a = inputPorts.AtIndex(0);
						Port b = inputPorts.AtIndex(1);

						// Swap connections
						Port connectedA = GetConnectedPort(a);
						Port connectedB = GetConnectedPort(b);
						DisconnectAllInputs(node);
						if (connectedA != null) Connect(connectedA, b);
						if (connectedB != null) Connect(connectedB, a);

						// Swap values (the values used if no port is connected)
						if (connectedA != null || connectedB != null) {
							// Node doesn't update properly unless at least one connection swaps
							var aSlot = GetMaterialSlot(a);
							var bSlot = GetMaterialSlot(b);
							var valueProperty = aSlot.GetType().GetProperty("value");
							var valueA = valueProperty.GetValue(aSlot);
							var valueB = valueProperty.GetValue(bSlot);
							valueProperty.SetValue(aSlot, valueB);
							valueProperty.SetValue(bSlot, valueA);
						}

						//if (connectedA == null && connectedB == null) {
						// If there's no connections we need to update values ourself
						//graphDataType.GetMethod("ValidateGraph").Invoke(graphData, null);
						//abstractMaterialNodeType.GetMethod("Dirty").Invoke(materialNode, new object[]{1});
						//graphDataType.GetMethod("ValidateGraph").Invoke(graphData, null);
						//}
					}
				}
			}
		}

		#endregion

		#region Extra Features (Add Node Commands)
		private static Type[] addNodeTypes = new Type[10];
		private static readonly string[] addNodeDefaults = new string[]{
			"Add",
			"Subtract",
			"Multiply",
			"Lerp",
			"Split",
			"OneMinus",
			"Negate",
			"Absolute",
			"Step",
			"Smoothstep"
		};

		#region Extra Features (Rebind)
		private class RebindWindow : EditorWindow {

			private string shortcutID = "Main Menu/Tools/SGVariablesExtraFeatures/Commands/Add Node ";
			private string[] shortcuts;
			private string[] values;

			//private IShortcutManager shortcutManager;

			void OnEnable() {
				IShortcutManager shortcutManager = ShortcutManager.instance;
				shortcuts = new string[10];
				values = new string[10];
				for (int i = 0; i < 10; i++) {
					ShortcutBinding shortcut = shortcutManager.GetShortcutBinding(shortcutID + (i + 1));
					shortcuts[i] = shortcut.ToString();

					string s = EditorPrefs.GetString("CyanSGVariables_Node" + (i + 1));
					if (s == null || s == "") {
						s = addNodeDefaults[i];
					}
					values[i] = s;
				}
			}

			void OnGUI() {
				GUILayout.Label("Add Node Bindings", EditorStyles.boldLabel);
				GUILayout.Label("Note : You can also change the hotkeys in Edit -> Shortcuts, search for SGVariables");
				GUILayout.Label("The values here should match the class used by a node. Should be the same as the node name, without spaces. It can't add SubGraphs. Probably won't support pipeline-specific nodes, only searches the UnityEngine.ShaderGraph namespace.");
				for (int i = 0; i < 10; i++) {
					EditorGUI.BeginChangeCheck();
					string s = EditorGUILayout.TextField("Node Binding " + (i + 1) + " (" + shortcuts[i] + ")", values[i]);
					if (EditorGUI.EndChangeCheck()) {
						EditorPrefs.SetString("CyanSGVariables_Node" + (i + 1), System.Text.RegularExpressions.Regex.Replace(s, @"\s+", ""));
						ClearTypes();
					}
				}
			}

			private void ClearTypes() {
				addNodeTypes = new Type[10];
			}
		}

		[MenuItem("Tools/SGVariablesExtraFeatures/Remap Node Bindings", false, 0)]
		private static void RebindNodes() {
			RebindWindow window = (RebindWindow)EditorWindow.GetWindow(typeof(RebindWindow));
			window.Show();
		}
		#endregion

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 1 _1")]
		private static void AddNodeCommand1() { AddNodeCommand(1); }

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 2 _2")]
		private static void AddNodeCommand2() { AddNodeCommand(2); }

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 3 _3")]
		private static void AddNodeCommand3() { AddNodeCommand(3); }

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 4 _4")]
		private static void AddNodeCommand4() { AddNodeCommand(4); }

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 5 _5")]
		private static void AddNodeCommand5() { AddNodeCommand(5); }

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 6 _6")]
		private static void AddNodeCommand6() { AddNodeCommand(6); }

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 7 _7")]
		private static void AddNodeCommand7() { AddNodeCommand(7); }

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 8 _8")]
		private static void AddNodeCommand8() { AddNodeCommand(8); }

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 9 _9")]
		private static void AddNodeCommand9() { AddNodeCommand(9); }

		[MenuItem("Tools/SGVariablesExtraFeatures/Commands/Add Node 10 _0")]
		private static void AddNodeCommand10() { AddNodeCommand(10); }

		private static void AddNodeCommand(int i) {
			if (graphView == null) return;
			Type type = addNodeTypes[i - 1];
			if (type == null) {
				string node = EditorPrefs.GetString("CyanSGVariables_Node" + i);
				if (node == null || node == "") {
					// Use default
					node = addNodeDefaults[i - 1];
				}
				string typeString = "UnityEditor.ShaderGraph." + node + "Node";
				type = sgAssembly.GetType(typeString);
				addNodeTypes[i - 1] = type;
				if (type == null) {
					Debug.LogWarning("Type " + typeString + " does not exist");
				}
			}
			Vector2 mousePos = Event.current.mousePosition;
			mousePos.y -= 35;
			Matrix4x4 matrix = graphView.viewTransform.matrix.inverse;
			Rect r = new Rect();
			r.position = matrix.MultiplyPoint(mousePos);
			AddNode(type, r);
		}

		private static void AddNode(Type type, Rect position) {
			if (type == null) return;
			var nodeToAdd = Activator.CreateInstance(type);
			if (nodeToAdd == null) {
				Debug.LogWarning("Could not create node of type " + type);
			}

			// https://github.com/Unity-Technologies/Graphics/blob/master/com.unity.shadergraph/Editor/Data/Nodes/AbstractMaterialNode.cs

			var drawStateProperty = abstractMaterialNodeType.GetProperty("drawState", bindingFlags); // Type : DrawState
			var drawState = drawStateProperty.GetValue(nodeToAdd);
			var positionProperty = drawState.GetType().GetProperty("position", bindingFlags);
			var expandedProperty = drawState.GetType().GetProperty("expanded", bindingFlags);

			positionProperty.SetValue(drawState, position);
			drawStateProperty.SetValue(nodeToAdd, drawState);

			// GraphData.AddNode(abstractMaterialNode)
			MethodInfo addNode = graphDataType.GetMethod("AddNode", bindingFlags);
			addNode.Invoke(graphData, new object[] { nodeToAdd });

			if (debugMessages) Debug.Log("Added Node of Type " + type.ToString());
		}
		#endregion

		#region Extra Features (Group Colours)

		private static string GetSerializedValue(Node node) {
			object materialNode = NodeToSGMaterialNode(node);
			if (materialNode != null) {
				string[] synonyms = GetSerializedValues(materialNode);
				if (synonyms != null && synonyms.Length > 0) {
					return synonyms[0];
				}
			}
			return "";
		}

		private static void SetSerializedValue(Node node, string key) {
			object materialNode = NodeToSGMaterialNode(node);
			SetSerializedValues(materialNode, new string[] { key });
		}

		private static Type colorPickerType;
		private static void GetColorPickerType() {
			// pretty hacky
			Assembly assembly = Assembly.Load(new AssemblyName("UnityEditor"));
			colorPickerType = assembly.GetType("UnityEditor.ColorPicker");
		}

		internal static void UpdateExtraFeatures() {
			//if (loadVariables) { // (first time load, but we kinda need to constantly check as groups could be copied)
			// Load Group Colours
			graphView.nodes.ForEach((Node node) => {
				if (node.title.Equals("Color") && node.visible) {
					if (GetSerializedValue(node) == "GroupColor") {
						var scope = node.GetContainingScope();
						if (scope == null) {
							// Node is not in group, the group may have been deleted.
							// GraphData.RemoveNode(abstractMaterialNode);
							MethodInfo addNode = graphDataType.GetMethod("RemoveNode", bindingFlags);
							addNode.Invoke(graphData, new object[] { NodeToSGMaterialNode(node) });
							return;
						}
						node.visible = false;
						node.style.maxWidth = 100;
						SetGroupColor(scope as Group, node, GetGroupNodeColor(node));
					}
				}
			});
			//}

			// As we right-click group, add the manipulator
			List<ISelectable> selected = graphView.selection;
			bool groupSelected = false;
			foreach (ISelectable s in selected) {
				if (s is Group) {
					Group group = (Group)s;
					groupSelected = true;

					// Set selection colour (if overriden)
					if (group.style.borderTopColor != StyleKeyword.Null) {
						Color selectedColor = new Color(0.266f, 0.75f, 1, 1);
						group.style.borderTopColor = selectedColor;
						group.style.borderBottomColor = selectedColor;
						group.style.borderLeftColor = selectedColor;
						group.style.borderRightColor = selectedColor;
					}

					if (editingGroup == s) break;

					if (editingGroup != null) {
						// (if switched to a different group)
						if (editingGroup.style.borderTopColor != StyleKeyword.Null) {
							var colorNode = GetGroupColorNode(GetGroupGuid(editingGroup));
							if (colorNode != null) {
								Color groupColor = GetGroupNodeColor(colorNode);
								editingGroup.style.borderTopColor = groupColor;
								editingGroup.style.borderBottomColor = groupColor;
								editingGroup.style.borderLeftColor = groupColor;
								editingGroup.style.borderRightColor = groupColor;
							}
						}
						editingGroup.RemoveManipulator(manipulator);
					}
					group.AddManipulator(manipulator);
					editingGroup = group;
					break;
				}
			}

			if (!groupSelected && editingGroup != null) {
				// (if group was unselected)
				if (editingGroup.style.borderTopColor != StyleKeyword.Null) {
					var colorNode = GetGroupColorNode(GetGroupGuid(editingGroup));
					if (colorNode != null) {
						Color groupColor = GetGroupNodeColor(colorNode);
						editingGroup.style.borderTopColor = groupColor;
						editingGroup.style.borderBottomColor = groupColor;
						editingGroup.style.borderLeftColor = groupColor;
						editingGroup.style.borderRightColor = groupColor;
					}
				}
				editingGroup = null;
			}

			// User clicked context menu, show Color Picker
			if (editingGuid != null) {
				Node colorNode = GetGroupColorNode(editingGuid);
				editingGroupColorNode = colorNode;
				if (colorNode != null) {
					if (colorNode.visible) {
						colorNode.visible = false;
						colorNode.style.maxWidth = 100;
					}

					// Couldn't figure out a way to show the colour picker from UIElements, so... reflection!
					// Show(Action<Color> colorChangedCallback, Color col, bool showAlpha = true, bool hdr = false)

					if (colorPickerType == null) GetColorPickerType();

					MethodInfo show = colorPickerType.GetMethod("Show",
						new Type[] { typeof(Action<Color>), typeof(Color), typeof(bool), typeof(bool) }
					);
					Action<Color> action = GroupColourChanged;
					show.Invoke(null, new object[] { action, GetGroupNodeColor(editingGroupColorNode), true, false });

					editingGuid = null;
				}
			} else {
				editingGroupColorNode = null;
			}
		}

		static void BuildContextualMenu(ContextualMenuPopulateEvent evt) {
			evt.menu.AppendSeparator();
			evt.menu.AppendAction("Edit Group Color", OnMenuAction, DropdownMenuAction.AlwaysEnabled);
		}

		static void OnMenuAction(DropdownMenuAction action) {
			if (editingGroup == null) return;
			//Debug.Log("OnMenuAction " + editingGroup.title);

			string guid = GetGroupGuid(editingGroup);
			if (guid == null) return;

			Node colorNode = GetGroupColorNode(guid);
			if (colorNode == null) {
				// We store the group colour in a hidden colour node
				CreateGroupColorNode(editingGroup);

				// We need to wait until this node is actually created,
				// so we'll store the guid in editingGuid and handle the rest
				// in UpdateExtraFeatures()
			}

			editingGuid = guid;
		}

		private static ContextualMenuManipulator manipulator = new ContextualMenuManipulator(BuildContextualMenu);

		private static Group editingGroup;
		private static string editingGuid;
		private static Node editingGroupColorNode;

		private static PropertyInfo groupGuidField;

		private static void CreateGroupColorNode(Group group) {
			var groupData = group.userData;
			if (groupData == null) return;

			var nodeToAdd = Activator.CreateInstance(colorNodeType); // Type : ColorNode

			// https://github.com/Unity-Technologies/Graphics/blob/master/com.unity.shadergraph/Editor/Data/Nodes/AbstractMaterialNode.cs

			FieldInfo synonymsField = abstractMaterialNodeType.GetField("synonyms");
			synonymsField.SetValue(nodeToAdd, new string[] { "GroupColor" });

			var a = colorNodeType.GetProperty("color"); // SG Color struct, not UnityEngine.Color
			var colorStruct = a.GetValue(nodeToAdd);
			var b = colorStruct.GetType().GetField("color");
			b.SetValue(colorStruct, new Color(0.1f, 0.1f, 0.1f, 0.1f));
			a.SetValue(nodeToAdd, colorStruct);

			var groupProperty = abstractMaterialNodeType.GetProperty("group", bindingFlags); // Type : GroupData
			var drawStateProperty = abstractMaterialNodeType.GetProperty("drawState", bindingFlags); // Type : DrawState
			var previewExpandedProperty = abstractMaterialNodeType.GetProperty("previewExpanded", bindingFlags); // Type : Bool

			previewExpandedProperty.SetValue(nodeToAdd, false);
			var drawState = drawStateProperty.GetValue(nodeToAdd);
			var positionProperty = drawState.GetType().GetProperty("position", bindingFlags);
			var expandedProperty = drawState.GetType().GetProperty("expanded", bindingFlags);
			Rect r = group.GetPosition();
			r.x += 25;
			r.y += 60;
			positionProperty.SetValue(drawState, r);
			expandedProperty.SetValue(drawState, false);
			drawStateProperty.SetValue(nodeToAdd, drawState);
			groupProperty.SetValue(nodeToAdd, groupData);

			// GraphData.AddNode(abstractMaterialNode)
			MethodInfo addNode = graphDataType.GetMethod("AddNode", bindingFlags);
			addNode.Invoke(graphData, new object[] { nodeToAdd });
		}

		private static string GetGroupGuid(Scope scope) {
			var groupData = scope.userData;
			if (groupData == null) return null;
			if (groupGuidField == null) groupGuidField = groupData.GetType().GetProperty("objectId", bindingFlags);
			var groupGuid = (string)groupGuidField.GetValue(groupData);
			//Debug.Log("GroupGuid : " + groupGuid);
			return groupGuid;
		}

		private static Node GetGroupColorNode(string guid) {
			List<Node> nodes = graphView.nodes.ToList();
			foreach (Node node in nodes) {
				if (node.title.Equals("Color")) {
					var scope = node.GetContainingScope();
					if (scope == null) continue; // Node is not in group
					if (GetGroupGuid(scope) == guid && GetSerializedValue(node) == "GroupColor") {
						//Debug.Log("Found node in group~");
						return node;
					}
				}
			}
			return null;
		}

		private static Color GetGroupNodeColor(Node node) {
			var materialNode = NodeToSGMaterialNode(node);
			var a = colorNodeType.GetProperty("color"); // SG Color struct, not UnityEngine.Color
			var colorStruct = a.GetValue(materialNode);
			var b = colorStruct.GetType().GetField("color");
			return (Color)b.GetValue(colorStruct);
		}

		private static void SetGroupColor(Group group, Node groupNode, Color color) {
			group.style.backgroundColor = color;

			// Change border too
			group.style.borderTopColor = color;
			group.style.borderBottomColor = color;
			group.style.borderLeftColor = color;
			group.style.borderRightColor = color;

			var materialNode = NodeToSGMaterialNode(groupNode);
			var a = colorNodeType.GetProperty("color"); // SG Color struct, not UnityEngine.Color
			var colorStruct = a.GetValue(materialNode);
			var b = colorStruct.GetType().GetField("color");
			b.SetValue(colorStruct, color);
			a.SetValue(materialNode, colorStruct);

			var label = group.Query<UnityEngine.UIElements.Label>().First();
			if (color.grayscale > 0.5 && color.a > 0.4) {
				//label.style.backgroundColor = new Color(0,0,0,0.5f);
				label.style.color = Color.black;
			} else {
				label.style.color = Color.white;
			}
		}

		private static void GroupColourChanged(Color color) {
			SetGroupColor(editingGroup, editingGroupColorNode, color);
		}
		#endregion
	}

}