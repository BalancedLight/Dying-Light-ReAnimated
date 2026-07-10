from dlanm2_gui.fbx_pipeline import ROOT_POLICIES


def test_public_root_policy_choices() -> None:
    assert ROOT_POLICIES == ("inplace", "bip01", "motion")
