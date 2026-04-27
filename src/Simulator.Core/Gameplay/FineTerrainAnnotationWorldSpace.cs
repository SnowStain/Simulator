using System.Numerics;

namespace Simulator.Core.Gameplay;

public static class FineTerrainAnnotationWorldSpace
{
    public static Matrix4x4 CreateModelToSceneMatrix(
        double fieldLengthM,
        double fieldWidthM,
        double modelCenterX,
        double modelCenterY,
        double modelCenterZ,
        double xMetersPerModelUnit,
        double yMetersPerModelUnit,
        double zMetersPerModelUnit)
    {
        float sx = (float)Math.Max(1e-6, xMetersPerModelUnit);
        float sy = (float)Math.Max(1e-6, yMetersPerModelUnit);
        float sz = (float)Math.Max(1e-6, zMetersPerModelUnit);
        float tx = (float)(fieldLengthM * 0.5 + modelCenterX * sx);
        float ty = (float)(-modelCenterY * sy);
        float tz = (float)(fieldWidthM * 0.5 + modelCenterZ * sz);
        return new Matrix4x4(
            -sx, 0f, 0f, 0f,
            0f, sy, 0f, 0f,
            0f, 0f, -sz, 0f,
            tx, ty, tz, 1f);
    }

    public static Matrix4x4 CreateModelToWorldMatrix(
        double fieldLengthM,
        double fieldWidthM,
        double metersPerWorldUnit,
        double modelCenterX,
        double modelCenterY,
        double modelCenterZ,
        double xMetersPerModelUnit,
        double yMetersPerModelUnit,
        double zMetersPerModelUnit)
    {
        double safeMetersPerWorldUnit = Math.Max(metersPerWorldUnit, 1e-6);
        Matrix4x4 scene = CreateModelToSceneMatrix(
            fieldLengthM,
            fieldWidthM,
            modelCenterX,
            modelCenterY,
            modelCenterZ,
            xMetersPerModelUnit,
            yMetersPerModelUnit,
            zMetersPerModelUnit);
        Matrix4x4 toWorldUnits = Matrix4x4.CreateScale(
            (float)(1.0 / safeMetersPerWorldUnit),
            1f,
            (float)(1.0 / safeMetersPerWorldUnit));
        return scene * toWorldUnits;
    }

    public static (double WorldX, double WorldY, double HeightM) ModelPointToWorld(
        double fieldLengthM,
        double fieldWidthM,
        double metersPerWorldUnit,
        double modelCenterX,
        double modelCenterY,
        double modelCenterZ,
        double xMetersPerModelUnit,
        double yMetersPerModelUnit,
        double zMetersPerModelUnit,
        double modelX,
        double modelY,
        double modelZ)
    {
        double safeMetersPerWorldUnit = Math.Max(metersPerWorldUnit, 1e-6);
        double centeredXMeters = (modelX - modelCenterX) * xMetersPerModelUnit;
        double centeredZMeters = (modelZ - modelCenterZ) * zMetersPerModelUnit;
        double heightM = (modelY - modelCenterY) * yMetersPerModelUnit;
        return (
            (fieldLengthM * 0.5 - centeredXMeters) / safeMetersPerWorldUnit,
            (fieldWidthM * 0.5 - centeredZMeters) / safeMetersPerWorldUnit,
            heightM);
    }

    public static Vector3 TransformCompositeModelPoint(
        Vector3 pivotModel,
        Vector3 positionModel,
        Vector3 rotationYprDegrees,
        Vector3 modelPoint)
    {
        float yaw = rotationYprDegrees.X * MathF.PI / 180f;
        float pitch = rotationYprDegrees.Y * MathF.PI / 180f;
        float roll = rotationYprDegrees.Z * MathF.PI / 180f;
        Matrix4x4 transform =
            Matrix4x4.CreateTranslation(-pivotModel)
            * Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll)
            * Matrix4x4.CreateTranslation(positionModel);
        return Vector3.Transform(modelPoint, transform);
    }

    public static (double WorldX, double WorldY, double HeightM) CompositeModelPointToWorld(
        double fieldLengthM,
        double fieldWidthM,
        double metersPerWorldUnit,
        double modelCenterX,
        double modelCenterY,
        double modelCenterZ,
        double xMetersPerModelUnit,
        double yMetersPerModelUnit,
        double zMetersPerModelUnit,
        Vector3 pivotModel,
        Vector3 positionModel,
        Vector3 rotationYprDegrees,
        Vector3 modelPoint)
    {
        Vector3 transformedPoint = TransformCompositeModelPoint(
            pivotModel,
            positionModel,
            rotationYprDegrees,
            modelPoint);
        return ModelPointToWorld(
            fieldLengthM,
            fieldWidthM,
            metersPerWorldUnit,
            modelCenterX,
            modelCenterY,
            modelCenterZ,
            xMetersPerModelUnit,
            yMetersPerModelUnit,
            zMetersPerModelUnit,
            transformedPoint.X,
            transformedPoint.Y,
            transformedPoint.Z);
    }
}
