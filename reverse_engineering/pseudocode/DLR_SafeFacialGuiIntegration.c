/* Named reconstruction of the safe facial GUI integration policy. */

void DLR_RebuildAnimationTableWithFacialColumnSafely(Table *table)
{
    /* Restore the host table shape; normal refresh creates fresh owned widgets. */
    Table_SetColumnCount(table, 9);
    DLR_HostRefreshAnimationTable(table);

    /* Qt shifts IK and Retarget cells. Do not remove/reuse owned cell widgets. */
    Table_InsertColumn(table, 7);
    DLR_PopulateBodyFaceControls(table, 7);
}

void DLR_RefreshFacialClipSelectorWithoutRecursion(Combo *combo)
{
    Combo_BlockSignals(combo, true);
    Combo_Clear(combo);
    DLR_AddProjectAnimations(combo);
    Combo_RestoreSelection(combo);
    Combo_BlockSignals(combo, false);
}

void DLR_InstallFacialUiAtomically(MainWindow *window)
{
    DLR_CreateProjectFacialControls(window);
    DLR_CreateAlwaysVisibleFacialTab(window);
    DLR_CreateAdvancedRootMotionSourceControl(window);
    DLR_WrapAnimationRefreshWithSafeColumnInsertion(window);
    window->mimic_ui_installed = true;
}
