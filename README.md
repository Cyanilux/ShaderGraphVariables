# Shader Graph Variables

- **Requires Shader Graph v10+**
- **Tested with Unity 2020.3.0f1, Shader Graph v10.3.2** (URP, but should work in HDRP too)
- Should also work with Unity 2021.1 / SG v11

If there's any errors/problems, let me know by opening an [issue](https://github.com/Cyanilux/ShaderGraphVariables/issues) (if one doesn't already exist)

<img src="https://user-images.githubusercontent.com/69320946/122465638-3e384180-cfb0-11eb-8912-30dc00234a46.gif" width="600">

### Main Feature :
- Adds `Register Variable` and `Get Variable` nodes to Shader Graph, allowing you to link sections of a graph without connection wires
  - These nodes (technically subgraphs) include a TextField where you can enter a variable name (not case sensitive)
  - They automatically link up with invisible connections/wires/edges
  - These variables are **local to the graph** - they won't be shared between other graphs or subgraphs
- Supports **Vector** and **Float** types
  - Vector2/3 will be promoted to Vector4. After `Get Variable`, can `Split` and re-`Combine` after if required
  - If a float is connected the port will automatically change. However note that DynamicVector/DynamicValue slots (used by most math nodes) currently default to the Vector4 port instead. If you require float, put the value through a `Float` node before connecting
- The variable names are serialized using the node's "synonyms" field, which is unused by the graph (only used for nodes in the Add Node menu). The field was added in v10 so is one main reason why the tool wouldn't work properly on previous versions (the SubGraphs also likely won't load)
- Note that if the tool is removed, any graphs that used it should still load correctly. However since it does use a few SubGraphs, if they don't exist anymore it will cause the graph to error and you'll need to remove those nodes, reinstall the tool, or at least include the SubGraphs from the tool in your Assets to fix it
- For setup & usage instructions, see below

### Extra Features :
- Group Colors
  - Right-click group name and select "Edit Group Color" to show Color Picker to edit background/border colour (inc. alpha)
  - Uses a hidden Color node & synonyms for serialization. It can't be moved so try to move the group as a whole rather than the nodes in it
<img src="https://user-images.githubusercontent.com/69320946/122465680-4b553080-cfb0-11eb-9f90-f6573de90084.gif" width="600">

- 'Port Swap' Hotkey (Default : S)
  - Swaps ports on selected nodes
  - Enabled for nodes : Add, Subtract, Multiply, Divide, Maximum, Minimum, Lerp, Inverse Lerp, Step and Smoothstep
<img src="https://user-images.githubusercontent.com/69320946/122465666-47c1a980-cfb0-11eb-918a-5e22c8423dde.gif" width="600">

- 'Add Node' Hotkeys (Default : Alpha Number keys, 1 to 0)
  - 10 hotkeys for quickly adding nodes at the mouse position. Defaults are :
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
  - To change nodes : Tools → SGVariables → ExtraFeatures → Rebind Node Bindings
- To edit keybindings : Edit → Shortcuts (search for SGVariables)
  - Note, try to avoid conflicts with [SG's hotkeys](https://www.cyanilux.com/tutorials/intro-to-shader-graph/#shortcuts) (mainly A, F and O) as those can't be rebound

### Setup:
- Install via Package Manager → Add package via git URL : `https://github.com/Cyanilux/ShaderGraphVariables.git`
- Alternatively, download and put the folder in your Assets
- Note these methods won't update automatically, so please check back if you have any problems. If there's any important fixes or additional features added, I'll likely post about it on [twitter](https://twitter.com/Cyanilux) too!

### Usage : 
1) Add Node → Register Variable
    - The node has a Text Field in the place of it's output port where you can type a variable name
    - Attach a Float/Vector to the input port
2) Add Node → Get Variable
    - Again, it has a Text Field but this time for the input port. Type the same variable name
    - Variable names aren't case sensitive. "Example" would stil link to "EXAMPLE" or "eXaMpLe" etc.
    - When the variable name matches, the input port value (e.g. (0,0,0,0)) should disappear and the preview will change
    - A connection/edge may blink temporarily, but then is hidden to keep the graph clean (kinda the whole point of the tool)
    - You can now use the output of that node as you would usually

### Authors :
- Cyanilux ([URP/SG Tutorials](https://www.cyanilux.com/), [Twitter](https://twitter.com/Cyanilux))

### Known Issues :
  - If a 'Get Variable' node is connected to the vertex stage and then a name is entered, it can cause shader errors if fragment-only nodes are used by the variable (e.g. cannot map expression to vs_5_0 instruction set)
  - If a node uses a DynamicVector/DynamicValue slot (Most math nodes) it currently will default to Vector4. If you want Float, pass the value through the Float node before connecting!
