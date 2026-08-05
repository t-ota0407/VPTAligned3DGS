import pycolmap
print("pycolmap", pycolmap.__version__)
rec = pycolmap.Reconstruction(r"scene_data\example1\hloc\ref_migrated")
print("cameras", len(rec.cameras), "images", len(rec.images))
print("accessing rigs ...")
print("num rigs:", len(rec.rigs))
print("OK rigs accessible")