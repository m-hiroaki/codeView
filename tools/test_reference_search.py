"""Reject incomplete or stale native GUI evidence."""
import copy
import unittest
from check_reference_search import CHECKS, validate


class ReferenceSearchEvidenceTests(unittest.TestCase):
    def report(self):
        return dict(Scenario='native-reference-search', Failure=None, ProductInformationalVersion='1.0+abc',
                    DisplayMode='full-document-candidate', ReferenceChecks=dict(Passed=True, Checks=list(CHECKS)))

    def test_complete_native_evidence(self):
        validate(self.report(), 'abc')

    def test_missing_or_duplicate_check_is_rejected(self):
        for checks in (CHECKS[:-1], CHECKS + CHECKS[:1], list(reversed(CHECKS))):
            report = self.report(); report['ReferenceChecks']['Checks'] = checks
            with self.assertRaises(ValueError): validate(report, 'abc')

    def test_failed_partial_or_stale_run_is_rejected(self):
        for key, value in [('Failure', 'failed'), ('Scenario', 'other'), ('ProductInformationalVersion', '1.0+old'),
                           ('DisplayMode', 'bounded-window-prototype'), ('ReferenceChecks', dict(Passed=False, Checks=CHECKS))]:
            report = copy.deepcopy(self.report()); report[key] = value
            with self.assertRaises(ValueError): validate(report, 'abc')

    def test_missing_report_is_not_success(self):
        with self.assertRaises(ValueError): validate({}, 'abc')


if __name__ == '__main__':
    unittest.main()
