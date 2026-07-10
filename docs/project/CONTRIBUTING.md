# Contributing

DL ReAnimated is under active development, and its project/GUI layer is intended to remain stable and extensible.

Before changing transform or codec behavior:

1. add or update a focused regression test;
2. preserve the stock rebuilt writer control;
3. preserve the 65-bone FBX T-pose matrix validation;
4. distinguish ANM2 pose data from movie/graph/gameplay accumulation;
5. distill confirmed editor observations into the relevant user or developer guide; keep raw research output outside the release tree.

Before changing project or mapping formats:

1. do not rename stable role IDs, root-policy values, or target IDs without migration;
2. add a one-step project/profile migration;
3. preserve unknown fields and extension data;
4. update the JSON Schema and compatibility docs;
5. add old-to-new and round-trip tests.

Before changing RPack append behavior:

1. preserve every known existing animation/script resource;
2. keep manifest hash verification;
3. reject unknown/unpreserved resource types instead of dropping them;
4. test both duplicate-error and duplicate-replace paths.

The GUI must remain a thin client over the project/build APIs. Features that only work through widgets and cannot be automated from a `.dlraproj` are not considered complete.

Run:

```bash
python -m pytest -q
python -m compileall -q dlanm2_gui
```
