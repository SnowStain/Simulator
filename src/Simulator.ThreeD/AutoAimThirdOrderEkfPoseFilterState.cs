namespace Simulator.ThreeD;

// 3阶 EKF 位姿滤波器：
// - 平面使用 [x, y, vx, vy, ax, ay] 常加速度状态
// - 观测使用相对射手的 [range, bearing] 非线性量测
// - 高度使用 [z, vz, az] 3阶 KF
internal sealed class AutoAimThirdOrderEkfPoseFilterState
{
    private const double InitialPositionVariance = 0.20 * 0.20;
    private const double InitialVelocityVariance = 4.5 * 4.5;
    private const double InitialAccelerationVariance = 16.0 * 16.0;

    private readonly double[] _planarState = new double[6];
    private readonly double[,] _planarCovariance = new double[6, 6];
    private readonly double[] _heightState = new double[3];
    private readonly double[,] _heightCovariance = new double[3, 3];

    private AutoAimThirdOrderEkfPoseFilterState(
        string observationKey,
        string targetId,
        string aimPlateId,
        string observationPlateId,
        string targetKind)
    {
        ObservationKey = observationKey;
        TargetId = targetId;
        AimPlateId = aimPlateId;
        ObservationPlateId = observationPlateId;
        TargetKind = targetKind;
    }

    public string ObservationKey { get; }

    public string TargetId { get; }

    public string AimPlateId { get; }

    public string ObservationPlateId { get; }

    public string TargetKind { get; }

    public bool Initialized { get; private set; }

    public bool HasLastMeasurement { get; set; }

    public double LastMeasurementXWorld { get; set; }

    public double LastMeasurementYWorld { get; set; }

    public double LastMeasurementHeightM { get; set; }

    public double LastMeasurementTimeSec { get; set; } = -999.0;

    public double LastUpdateTimeSec { get; set; } = -999.0;

    public double LastMetersPerWorldUnit { get; private set; } = 1.0;

    public double FilteredXWorld => _planarState[0];

    public double FilteredYWorld => _planarState[1];

    public double FilteredHeightM => _heightState[0];

    public double FilteredVelocityXMps => _planarState[2] * LastMetersPerWorldUnit;

    public double FilteredVelocityYMps => _planarState[3] * LastMetersPerWorldUnit;

    public double FilteredVelocityZMps => _heightState[1];

    public double FilteredAccelerationXMps2 => _planarState[4] * LastMetersPerWorldUnit;

    public double FilteredAccelerationYMps2 => _planarState[5] * LastMetersPerWorldUnit;

    public double FilteredAccelerationZMps2 => _heightState[2];

    public static AutoAimThirdOrderEkfPoseFilterState Create(
        string observationKey,
        string targetId,
        string aimPlateId,
        string observationPlateId,
        string targetKind)
        => new(observationKey, targetId, aimPlateId, observationPlateId, targetKind);

    public void Initialize(
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double velocityXMps,
        double velocityYMps,
        double velocityZMps,
        double metersPerWorldUnit)
    {
        LastMetersPerWorldUnit = Math.Max(metersPerWorldUnit, 1e-6);
        Array.Clear(_planarState, 0, _planarState.Length);
        Array.Clear(_heightState, 0, _heightState.Length);
        ClearMatrix(_planarCovariance);
        ClearMatrix(_heightCovariance);

        _planarState[0] = observedXWorld;
        _planarState[1] = observedYWorld;
        _planarState[2] = velocityXMps / LastMetersPerWorldUnit;
        _planarState[3] = velocityYMps / LastMetersPerWorldUnit;
        _planarState[4] = 0.0;
        _planarState[5] = 0.0;

        _heightState[0] = observedHeightM;
        _heightState[1] = velocityZMps;
        _heightState[2] = 0.0;

        for (int index = 0; index < 6; index++)
        {
            _planarCovariance[index, index] = index switch
            {
                0 or 1 => InitialPositionVariance,
                2 or 3 => InitialVelocityVariance,
                _ => InitialAccelerationVariance,
            };
        }

        for (int index = 0; index < 3; index++)
        {
            _heightCovariance[index, index] = index switch
            {
                0 => InitialPositionVariance,
                1 => InitialVelocityVariance,
                _ => InitialAccelerationVariance,
            };
        }

        Initialized = true;
    }

    public void Update(
        double observedXWorld,
        double observedYWorld,
        double observedHeightM,
        double shooterXWorld,
        double shooterYWorld,
        double dtSec,
        double metersPerWorldUnit,
        double measurementNoiseM,
        double jerkNoiseMps3)
    {
        LastMetersPerWorldUnit = Math.Max(metersPerWorldUnit, 1e-6);
        double dt = Math.Clamp(dtSec, 1.0 / 240.0, 0.12);
        double jerkVarianceWorld = Math.Pow(jerkNoiseMps3 / LastMetersPerWorldUnit, 2);
        double jerkVarianceMetric = jerkNoiseMps3 * jerkNoiseMps3;

        PredictPlanar(dt, jerkVarianceWorld);
        CorrectPlanar(observedXWorld, observedYWorld, shooterXWorld, shooterYWorld, measurementNoiseM);
        PredictHeight(dt, jerkVarianceMetric);
        CorrectHeight(observedHeightM, measurementNoiseM * measurementNoiseM);
    }

    private void PredictPlanar(double dt, double jerkVarianceWorld)
    {
        double dt2 = dt * dt;
        double halfDt2 = 0.5 * dt2;

        _planarState[0] += _planarState[2] * dt + _planarState[4] * halfDt2;
        _planarState[1] += _planarState[3] * dt + _planarState[5] * halfDt2;
        _planarState[2] += _planarState[4] * dt;
        _planarState[3] += _planarState[5] * dt;

        double[,] f = new double[6, 6]
        {
            { 1.0, 0.0, dt, 0.0, halfDt2, 0.0 },
            { 0.0, 1.0, 0.0, dt, 0.0, halfDt2 },
            { 0.0, 0.0, 1.0, 0.0, dt, 0.0 },
            { 0.0, 0.0, 0.0, 1.0, 0.0, dt },
            { 0.0, 0.0, 0.0, 0.0, 1.0, 0.0 },
            { 0.0, 0.0, 0.0, 0.0, 0.0, 1.0 },
        };

        double[,] q = new double[6, 6];
        WriteThirdOrderProcessNoiseBlock(q, 0, 2, 4, dt, jerkVarianceWorld);
        WriteThirdOrderProcessNoiseBlock(q, 1, 3, 5, dt, jerkVarianceWorld);

        double[,] predicted = Multiply(Multiply(f, _planarCovariance), Transpose(f));
        AddInPlace(predicted, q);
        CopyMatrix(predicted, _planarCovariance);
    }

    private void CorrectPlanar(
        double observedXWorld,
        double observedYWorld,
        double shooterXWorld,
        double shooterYWorld,
        double measurementNoiseM)
    {
        double dx = _planarState[0] - shooterXWorld;
        double dy = _planarState[1] - shooterYWorld;
        double predictedRangeWorld = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
        double predictedBearingRad = Math.Atan2(dy, dx);

        double measuredDx = observedXWorld - shooterXWorld;
        double measuredDy = observedYWorld - shooterYWorld;
        double measuredRangeWorld = Math.Max(1e-6, Math.Sqrt(measuredDx * measuredDx + measuredDy * measuredDy));
        double measuredBearingRad = Math.Atan2(measuredDy, measuredDx);

        double[,] h = new double[2, 6];
        double invRangeWorld = 1.0 / predictedRangeWorld;
        double invRangeWorldSq = invRangeWorld * invRangeWorld;
        h[0, 0] = dx * invRangeWorld;
        h[0, 1] = dy * invRangeWorld;
        h[1, 0] = -dy * invRangeWorldSq;
        h[1, 1] = dx * invRangeWorldSq;

        double rangeVarianceWorld = Math.Pow(measurementNoiseM / Math.Max(LastMetersPerWorldUnit, 1e-6), 2);
        double measuredRangeM = Math.Max(0.15, measuredRangeWorld * LastMetersPerWorldUnit);
        double bearingStdRad = Math.Clamp(measurementNoiseM / measuredRangeM, 0.0012, 0.26);
        double[,] r = new double[2, 2]
        {
            { rangeVarianceWorld, 0.0 },
            { 0.0, bearingStdRad * bearingStdRad },
        };

        double[,] hp = Multiply(h, _planarCovariance);
        double[,] s = Add(Multiply(hp, Transpose(h)), r);
        if (!TryInvert2x2(s, out double[,] sInv))
        {
            return;
        }

        double[,] k = Multiply(Multiply(_planarCovariance, Transpose(h)), sInv);
        double[] innovation =
        {
            measuredRangeWorld - predictedRangeWorld,
            NormalizeAngleRad(measuredBearingRad - predictedBearingRad),
        };

        double[] stateDelta = Multiply(k, innovation);
        for (int index = 0; index < _planarState.Length; index++)
        {
            _planarState[index] += stateDelta[index];
        }

        double[,] identity = Identity(6);
        double[,] kh = Multiply(k, h);
        double[,] corrected = Multiply(Subtract(identity, kh), _planarCovariance);
        SymmetrizeInPlace(corrected);
        CopyMatrix(corrected, _planarCovariance);
    }

    private void PredictHeight(double dt, double jerkVarianceMetric)
    {
        double dt2 = dt * dt;
        double halfDt2 = 0.5 * dt2;
        _heightState[0] += _heightState[1] * dt + _heightState[2] * halfDt2;
        _heightState[1] += _heightState[2] * dt;

        double[,] f = new double[3, 3]
        {
            { 1.0, dt, halfDt2 },
            { 0.0, 1.0, dt },
            { 0.0, 0.0, 1.0 },
        };
        double[,] q = new double[3, 3];
        WriteThirdOrderProcessNoiseBlock(q, 0, 1, 2, dt, jerkVarianceMetric);
        double[,] predicted = Multiply(Multiply(f, _heightCovariance), Transpose(f));
        AddInPlace(predicted, q);
        CopyMatrix(predicted, _heightCovariance);
    }

    private void CorrectHeight(double observedHeightM, double measurementVariance)
    {
        double innovation = observedHeightM - _heightState[0];
        double s = _heightCovariance[0, 0] + Math.Max(1e-8, measurementVariance);
        double k0 = _heightCovariance[0, 0] / s;
        double k1 = _heightCovariance[1, 0] / s;
        double k2 = _heightCovariance[2, 0] / s;

        _heightState[0] += k0 * innovation;
        _heightState[1] += k1 * innovation;
        _heightState[2] += k2 * innovation;

        double[,] corrected = new double[3, 3];
        double[] k = { k0, k1, k2 };
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                double hCol = col == 0 ? 1.0 : 0.0;
                corrected[row, col] = _heightCovariance[row, col] - k[row] * hCol * _heightCovariance[0, col];
            }
        }

        SymmetrizeInPlace(corrected);
        CopyMatrix(corrected, _heightCovariance);
    }

    private static void WriteThirdOrderProcessNoiseBlock(double[,] target, int posIndex, int velIndex, int accIndex, double dt, double jerkVariance)
    {
        double dt2 = dt * dt;
        double dt3 = dt2 * dt;
        double dt4 = dt2 * dt2;
        double dt5 = dt4 * dt;
        target[posIndex, posIndex] += dt5 / 20.0 * jerkVariance;
        target[posIndex, velIndex] += dt4 / 8.0 * jerkVariance;
        target[posIndex, accIndex] += dt3 / 6.0 * jerkVariance;
        target[velIndex, posIndex] += dt4 / 8.0 * jerkVariance;
        target[velIndex, velIndex] += dt3 / 3.0 * jerkVariance;
        target[velIndex, accIndex] += dt2 / 2.0 * jerkVariance;
        target[accIndex, posIndex] += dt3 / 6.0 * jerkVariance;
        target[accIndex, velIndex] += dt2 / 2.0 * jerkVariance;
        target[accIndex, accIndex] += dt * jerkVariance;
    }

    private static double[,] Identity(int size)
    {
        double[,] matrix = new double[size, size];
        for (int index = 0; index < size; index++)
        {
            matrix[index, index] = 1.0;
        }

        return matrix;
    }

    private static double[,] Add(double[,] left, double[,] right)
    {
        int rows = left.GetLength(0);
        int cols = left.GetLength(1);
        double[,] result = new double[rows, cols];
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                result[row, col] = left[row, col] + right[row, col];
            }
        }

        return result;
    }

    private static void AddInPlace(double[,] target, double[,] right)
    {
        int rows = target.GetLength(0);
        int cols = target.GetLength(1);
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                target[row, col] += right[row, col];
            }
        }
    }

    private static double[,] Subtract(double[,] left, double[,] right)
    {
        int rows = left.GetLength(0);
        int cols = left.GetLength(1);
        double[,] result = new double[rows, cols];
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                result[row, col] = left[row, col] - right[row, col];
            }
        }

        return result;
    }

    private static double[,] Multiply(double[,] left, double[,] right)
    {
        int leftRows = left.GetLength(0);
        int leftCols = left.GetLength(1);
        int rightCols = right.GetLength(1);
        double[,] result = new double[leftRows, rightCols];
        for (int row = 0; row < leftRows; row++)
        {
            for (int mid = 0; mid < leftCols; mid++)
            {
                double value = left[row, mid];
                if (Math.Abs(value) <= 1e-12)
                {
                    continue;
                }

                for (int col = 0; col < rightCols; col++)
                {
                    result[row, col] += value * right[mid, col];
                }
            }
        }

        return result;
    }

    private static double[] Multiply(double[,] left, double[] right)
    {
        int rows = left.GetLength(0);
        int cols = left.GetLength(1);
        double[] result = new double[rows];
        for (int row = 0; row < rows; row++)
        {
            double sum = 0.0;
            for (int col = 0; col < cols; col++)
            {
                sum += left[row, col] * right[col];
            }

            result[row] = sum;
        }

        return result;
    }

    private static double[,] Transpose(double[,] matrix)
    {
        int rows = matrix.GetLength(0);
        int cols = matrix.GetLength(1);
        double[,] result = new double[cols, rows];
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                result[col, row] = matrix[row, col];
            }
        }

        return result;
    }

    private static bool TryInvert2x2(double[,] matrix, out double[,] inverse)
    {
        inverse = new double[2, 2];
        double determinant = matrix[0, 0] * matrix[1, 1] - matrix[0, 1] * matrix[1, 0];
        if (Math.Abs(determinant) <= 1e-10)
        {
            return false;
        }

        double invDet = 1.0 / determinant;
        inverse[0, 0] = matrix[1, 1] * invDet;
        inverse[0, 1] = -matrix[0, 1] * invDet;
        inverse[1, 0] = -matrix[1, 0] * invDet;
        inverse[1, 1] = matrix[0, 0] * invDet;
        return true;
    }

    private static void SymmetrizeInPlace(double[,] matrix)
    {
        int size = matrix.GetLength(0);
        for (int row = 0; row < size; row++)
        {
            matrix[row, row] = Math.Max(1e-10, matrix[row, row]);
            for (int col = row + 1; col < size; col++)
            {
                double average = 0.5 * (matrix[row, col] + matrix[col, row]);
                matrix[row, col] = average;
                matrix[col, row] = average;
            }
        }
    }

    private static void CopyMatrix(double[,] source, double[,] target)
    {
        int rows = source.GetLength(0);
        int cols = source.GetLength(1);
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                target[row, col] = source[row, col];
            }
        }
    }

    private static void ClearMatrix(double[,] matrix)
    {
        for (int row = 0; row < matrix.GetLength(0); row++)
        {
            for (int col = 0; col < matrix.GetLength(1); col++)
            {
                matrix[row, col] = 0.0;
            }
        }
    }

    private static double NormalizeAngleRad(double radians)
    {
        double wrapped = radians % (Math.PI * 2.0);
        if (wrapped > Math.PI)
        {
            wrapped -= Math.PI * 2.0;
        }
        else if (wrapped < -Math.PI)
        {
            wrapped += Math.PI * 2.0;
        }

        return wrapped;
    }
}
